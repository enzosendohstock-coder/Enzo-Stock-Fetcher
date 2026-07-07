using System.Text.Json;

namespace PPI.Stock.Fetcher;

/// <summary>
/// 呼叫證券櫃檯買賣中心(TPEx)公開的三大法人買賣明細 OpenAPI，僅涵蓋上櫃股票。
/// 注意：此 API 只提供「最新一個交易日」的資料，不支援指定歷史日期查詢，因此不能用來回補過去的資料。
/// 官方文件的 JSON 欄位名稱有不一致的空白字元(例如欄位前後多了空格)，
/// 這裡一律先去除所有欄位名稱中的空白字元再比對，避免因為官方資料的小瑕疵而解析失敗。
/// </summary>
public class TpexClient
{
    private const string Url = "https://www.tpex.org.tw/openapi/v1/tpex_3insti_daily_trading";

    private const string ForeignExDealerBase = "ForeignInvestorsincludeMainlandAreaInvestors(ForeignDealersexcluded)";
    private const string ForeignDealerBase = "ForeignDealers";
    private const string ForeignTotalBase = "ForeignInvestorsIncludeMainlandAreaInvestors";
    private const string TrustBase = "SecuritiesInvestmentTrustCompanies";
    private const string DealerTotalBase = "Dealers";

    private readonly HttpClient _httpClient;

    public TpexClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<Dictionary<string, InstitutionalTradeDetail>> GetInstitutionalTradesAsync(DateOnly date)
    {
        using var response = await _httpClient.GetAsync(Url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
        {
            return new Dictionary<string, InstitutionalTradeDetail>();
        }

        var firstRow = Normalize(root[0]);
        var actualDate = ParseRocDate(firstRow["Date"]);
        if (actualDate != date)
        {
            Console.WriteLine($"警告：TPEx 開放資料目前只有 {actualDate:yyyy-MM-dd} 的資料，無法取得 {date:yyyy-MM-dd}（TPEx OpenAPI 不支援指定歷史日期查詢，上櫃股票只能抓當天）。");
            return new Dictionary<string, InstitutionalTradeDetail>();
        }

        var result = new Dictionary<string, InstitutionalTradeDetail>();
        foreach (var rawRow in root.EnumerateArray())
        {
            var row = Normalize(rawRow);
            var code = row["SecuritiesCompanyCode"].Trim();
            if (string.IsNullOrEmpty(code))
            {
                continue;
            }

            var exDealerBuy = Num(row, $"{ForeignExDealerBase}-TotalBuy");
            var exDealerSell = Num(row, $"{ForeignExDealerBase}-TotalSell");
            var exDealerNet = Num(row, $"{ForeignExDealerBase}-Difference");

            var foreignDealerBuy = Num(row, $"{ForeignDealerBase}-TotalBuy");
            var foreignDealerSell = Num(row, $"{ForeignDealerBase}-TotalSell");
            var foreignDealerNet = Num(row, $"{ForeignDealerBase}-Difference");

            result[code] = new InstitutionalTradeDetail
            {
                StockCode = code,
                StockName = row["CompanyName"].Trim(),
                Market = Market.Otc,

                ForeignExDealerBuy = exDealerBuy,
                ForeignExDealerSell = exDealerSell,
                ForeignExDealerNet = exDealerNet,

                ForeignDealerBuy = foreignDealerBuy,
                ForeignDealerSell = foreignDealerSell,
                ForeignDealerNet = foreignDealerNet,

                ForeignTotalBuy = Num(row, $"{ForeignTotalBase}-TotalBuy"),
                ForeignTotalSell = Num(row, $"{ForeignTotalBase}-TotalSell"),
                ForeignTotalNet = Num(row, $"{ForeignTotalBase}-Difference"),

                TrustBuy = Num(row, $"{TrustBase}-TotalBuy"),
                TrustSell = Num(row, $"{TrustBase}-TotalSell"),
                TrustNet = Num(row, $"{TrustBase}-Difference"),

                // 上櫃股票的自營商買賣超不分「自行買賣」與「避險」，官方只給合計。
                DealerSelfBuy = null,
                DealerSelfSell = null,
                DealerSelfNet = null,
                DealerHedgeBuy = null,
                DealerHedgeSell = null,
                DealerHedgeNet = null,

                DealerTotalBuy = Num(row, $"{DealerTotalBase}-TotalBuy"),
                DealerTotalSell = Num(row, $"{DealerTotalBase}-TotalSell"),
                DealerTotalNet = Num(row, $"{DealerTotalBase}-Difference"),

                GrandTotalNet = Num(row, "TotalDifference"),
            };
        }

        return result;
    }

    /// <summary>
    /// 將 JSON 物件的欄位名稱去除所有空白字元後，轉成 Dictionary 方便查找。
    /// </summary>
    private static Dictionary<string, string> Normalize(JsonElement obj)
    {
        var dict = new Dictionary<string, string>();
        foreach (var prop in obj.EnumerateObject())
        {
            var key = new string(prop.Name.Where(c => !char.IsWhiteSpace(c)).ToArray());
            // TPEx 偶爾會把某些欄位回傳成 JSON 數字而非字串(型別不一致)，一律用原始文字讀出避免解析炸掉。
            dict[key] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? "",
                JsonValueKind.Null => "",
                _ => prop.Value.GetRawText(),
            };
        }
        return dict;
    }

    private static long Num(Dictionary<string, string> row, string key) =>
        row.TryGetValue(key, out var raw) && long.TryParse(raw.Trim().Replace(",", ""), out var v) ? v : 0;

    /// <summary>
    /// 將民國日期字串(例如 "1150703")轉成西元日期。
    /// </summary>
    private static DateOnly ParseRocDate(string rocDate)
    {
        var month = int.Parse(rocDate.Substring(rocDate.Length - 4, 2));
        var day = int.Parse(rocDate.Substring(rocDate.Length - 2, 2));
        var rocYear = int.Parse(rocDate[..(rocDate.Length - 4)]);
        return new DateOnly(rocYear + 1911, month, day);
    }
}
