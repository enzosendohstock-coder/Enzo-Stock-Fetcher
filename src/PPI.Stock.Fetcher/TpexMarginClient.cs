using System.Text.Json;

namespace PPI.Stock.Fetcher;

/// <summary>
/// 呼叫 TPEx 公開的融資融券餘額(tpex_mainboard_margin_balance)與融券借券賣出餘額
/// (tpex_margin_sbl) OpenAPI，合併成每支股票一筆完整的 MarginTradingDetail，僅涵蓋上櫃股票。
/// 注意：跟三大法人買賣超的 TpexClient 一樣，只提供「最新一個交易日」的資料，不支援指定歷史日期查詢。
/// </summary>
public class TpexMarginClient
{
    private const string MarginUrl = "https://www.tpex.org.tw/openapi/v1/tpex_mainboard_margin_balance";
    private const string SblUrl = "https://www.tpex.org.tw/openapi/v1/tpex_margin_sbl";

    private readonly HttpClient _httpClient;

    public TpexMarginClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<Dictionary<string, MarginTradingDetail>> GetMarginTradingAsync(DateOnly date)
    {
        var marginTask = _httpClient.GetAsync(MarginUrl);
        var sblTask = _httpClient.GetAsync(SblUrl);
        await Task.WhenAll(marginTask, sblTask);

        var marginRows = await ParseMarginAsync(await marginTask, date);
        var sblRows = await ParseSblAsync(await sblTask, date);

        var result = new Dictionary<string, MarginTradingDetail>();
        foreach (var (code, row) in marginRows)
        {
            sblRows.TryGetValue(code, out var sbl);

            result[code] = new MarginTradingDetail
            {
                StockCode = code,
                StockName = row["CompanyName"].Trim(),
                Market = Market.Otc,

                // tpex_mainboard_margin_balance 的數字單位是「張」，跟 TWSE MI_MARGN 一樣，
                // 乘以 1000 換算成股，統一跟借券賣出(已經是股)的欄位單位一致。
                MarginBuy = NumLots(row, "MarginPurchase"),
                MarginSell = NumLots(row, "MarginSales"),
                MarginCashRedemption = NumLots(row, "CashRedemption"),
                MarginBalancePrev = NumLots(row, "MarginPurchaseBalancePreviousDay"),
                MarginBalance = NumLots(row, "MarginPurchaseBalance"),
                MarginQuota = NumLots(row, "MarginPurchaseQuota"),

                ShortSell = NumLots(row, "ShortSale"),
                ShortBuy = NumLots(row, "ShortConvering"),
                ShortStockRedemption = NumLots(row, "StockRedemption"),
                ShortBalancePrev = NumLots(row, "ShortSaleBalancePreviousDay"),
                ShortBalance = NumLots(row, "ShortSaleBalance"),
                ShortQuota = NumLots(row, "ShortSaleQuota"),

                Offsetting = NumLots(row, "Offsetting"),

                SblBalancePrev = sbl != null ? Num(sbl, "SecuritiesBorrowingBalancePreviousDay") : 0,
                SblSell = sbl != null ? Num(sbl, "SecuritiesBorrowingSale") : 0,
                SblReturn = sbl != null ? Num(sbl, "SecuritiesBorrowingReturn") : 0,
                SblAdjustment = sbl != null ? Num(sbl, "SecuritiesBorrowingAdjustment") : 0,
                SblBalance = sbl != null ? Num(sbl, "SecuritiesBorrowingBalanceOfTheMarketDay") : 0,
                SblQuota = sbl != null ? Num(sbl, "AvailableVolumesForSBLShortSale") : 0,
            };
        }

        return result;
    }

    private static async Task<Dictionary<string, Dictionary<string, string>>> ParseMarginAsync(HttpResponseMessage response, DateOnly date)
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
        {
            return result;
        }

        var firstRow = Normalize(root[0]);
        if (!IsRequestedDate(firstRow, date))
        {
            Console.WriteLine($"警告：TPEx 融資融券餘額開放資料目前只有最新一天的資料，無法取得 {date:yyyy-MM-dd}（TPEx OpenAPI 不支援指定歷史日期查詢）。");
            return result;
        }

        foreach (var rawRow in root.EnumerateArray())
        {
            var row = Normalize(rawRow);
            var code = row["SecuritiesCompanyCode"].Trim();
            if (string.IsNullOrEmpty(code))
            {
                continue;
            }
            result[code] = row;
        }

        return result;
    }

    private static async Task<Dictionary<string, Dictionary<string, string>>> ParseSblAsync(HttpResponseMessage response, DateOnly date)
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
        {
            return result;
        }

        foreach (var rawRow in root.EnumerateArray())
        {
            var row = Normalize(rawRow);
            var code = row["SecuritiesCompanyCode"].Trim();
            if (string.IsNullOrEmpty(code))
            {
                continue;
            }
            result[code] = row;
        }

        return result;
    }

    private static bool IsRequestedDate(Dictionary<string, string> firstRow, DateOnly date)
    {
        if (!firstRow.TryGetValue("Date", out var rocDate) || string.IsNullOrEmpty(rocDate))
        {
            return false;
        }
        return ParseRocDate(rocDate) == date;
    }

    /// <summary>
    /// 將 JSON 物件的欄位名稱去除所有空白字元後，轉成 Dictionary 方便查找，跟 TpexClient 做法一致。
    /// </summary>
    private static Dictionary<string, string> Normalize(JsonElement obj)
    {
        var dict = new Dictionary<string, string>();
        foreach (var prop in obj.EnumerateObject())
        {
            var key = new string(prop.Name.Where(c => !char.IsWhiteSpace(c)).ToArray());
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

    // tpex_mainboard_margin_balance 的數字單位是「張」，乘以 1000 換算成股。
    private static long NumLots(Dictionary<string, string> row, string key) => Num(row, key) * 1000;

    private static DateOnly ParseRocDate(string rocDate)
    {
        var month = int.Parse(rocDate.Substring(rocDate.Length - 4, 2));
        var day = int.Parse(rocDate.Substring(rocDate.Length - 2, 2));
        var rocYear = int.Parse(rocDate[..(rocDate.Length - 4)]);
        return new DateOnly(rocYear + 1911, month, day);
    }
}
