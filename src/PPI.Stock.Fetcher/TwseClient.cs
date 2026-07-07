using System.Text.Json;

namespace PPI.Stock.Fetcher;

/// <summary>
/// 呼叫台灣證券交易所(TWSE)公開的三大法人買賣超日報表(T86) API，僅涵蓋上市股票。
/// 支援任意歷史日期查詢(date 參數)。
/// </summary>
public class TwseClient
{
    private readonly HttpClient _httpClient;

    public TwseClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// 取得指定日期，全市場上市股票的三大法人買賣超明細。
    /// 若當天非交易日(假日)，回傳空字典。
    /// </summary>
    public async Task<Dictionary<string, InstitutionalTradeDetail>> GetInstitutionalTradesAsync(DateOnly date)
    {
        var dateStr = date.ToString("yyyyMMdd");
        var url = $"https://www.twse.com.tw/rwd/zh/fund/T86?date={dateStr}&selectType=ALL&response=json";

        using var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var stat = root.TryGetProperty("stat", out var statEl) ? statEl.GetString() : null;
        if (stat != "OK" || !root.TryGetProperty("data", out var dataEl) || !root.TryGetProperty("fields", out var fieldsEl))
        {
            // 非交易日或當天尚未公布資料
            return new Dictionary<string, InstitutionalTradeDetail>();
        }

        var fields = fieldsEl.EnumerateArray().Select(f => f.GetString() ?? "").ToArray();
        int Idx(string name)
        {
            var i = Array.IndexOf(fields, name);
            if (i < 0)
            {
                throw new InvalidOperationException($"TWSE T86 回應欄位格式異常，找不到「{name}」欄位。");
            }
            return i;
        }

        var codeIdx = Idx("證券代號");
        var nameIdx = Idx("證券名稱");
        var exDealerBuyIdx = Idx("外陸資買進股數(不含外資自營商)");
        var exDealerSellIdx = Idx("外陸資賣出股數(不含外資自營商)");
        var exDealerNetIdx = Idx("外陸資買賣超股數(不含外資自營商)");
        var dealerForeignBuyIdx = Idx("外資自營商買進股數");
        var dealerForeignSellIdx = Idx("外資自營商賣出股數");
        var dealerForeignNetIdx = Idx("外資自營商買賣超股數");
        var trustBuyIdx = Idx("投信買進股數");
        var trustSellIdx = Idx("投信賣出股數");
        var trustNetIdx = Idx("投信買賣超股數");
        var dealerSelfBuyIdx = Idx("自營商買進股數(自行買賣)");
        var dealerSelfSellIdx = Idx("自營商賣出股數(自行買賣)");
        var dealerSelfNetIdx = Idx("自營商買賣超股數(自行買賣)");
        var dealerHedgeBuyIdx = Idx("自營商買進股數(避險)");
        var dealerHedgeSellIdx = Idx("自營商賣出股數(避險)");
        var dealerHedgeNetIdx = Idx("自營商買賣超股數(避險)");
        var grandTotalIdx = Idx("三大法人買賣超股數");

        long Num(string[] cells, int idx) =>
            long.TryParse(cells[idx].Trim().Replace(",", ""), out var v) ? v : 0;

        // TWSE 偶爾會把某些儲存格回傳成 JSON 數字而非字串(型別不一致)，一律用原始文字讀出避免解析炸掉。
        static string CellToString(JsonElement c) => c.ValueKind switch
        {
            JsonValueKind.String => c.GetString() ?? "",
            JsonValueKind.Null => "",
            _ => c.GetRawText(),
        };

        var result = new Dictionary<string, InstitutionalTradeDetail>();
        foreach (var row in dataEl.EnumerateArray())
        {
            var cells = row.EnumerateArray().Select(CellToString).ToArray();
            if (cells.Length < fields.Length)
            {
                // TWSE 偶爾會回傳欄位數量不足的列(資料瑕疵)，跳過避免索引超出範圍。
                continue;
            }

            var code = cells[codeIdx].Trim();

            var exDealerBuy = Num(cells, exDealerBuyIdx);
            var exDealerSell = Num(cells, exDealerSellIdx);
            var exDealerNet = Num(cells, exDealerNetIdx);
            var dealerForeignBuy = Num(cells, dealerForeignBuyIdx);
            var dealerForeignSell = Num(cells, dealerForeignSellIdx);
            var dealerForeignNet = Num(cells, dealerForeignNetIdx);
            var dealerSelfBuy = Num(cells, dealerSelfBuyIdx);
            var dealerSelfSell = Num(cells, dealerSelfSellIdx);
            var dealerSelfNet = Num(cells, dealerSelfNetIdx);
            var dealerHedgeBuy = Num(cells, dealerHedgeBuyIdx);
            var dealerHedgeSell = Num(cells, dealerHedgeSellIdx);
            var dealerHedgeNet = Num(cells, dealerHedgeNetIdx);

            result[code] = new InstitutionalTradeDetail
            {
                StockCode = code,
                StockName = cells[nameIdx].Trim(),
                Market = Market.Listed,

                ForeignExDealerBuy = exDealerBuy,
                ForeignExDealerSell = exDealerSell,
                ForeignExDealerNet = exDealerNet,

                ForeignDealerBuy = dealerForeignBuy,
                ForeignDealerSell = dealerForeignSell,
                ForeignDealerNet = dealerForeignNet,

                ForeignTotalBuy = exDealerBuy + dealerForeignBuy,
                ForeignTotalSell = exDealerSell + dealerForeignSell,
                ForeignTotalNet = exDealerNet + dealerForeignNet,

                TrustBuy = Num(cells, trustBuyIdx),
                TrustSell = Num(cells, trustSellIdx),
                TrustNet = Num(cells, trustNetIdx),

                DealerSelfBuy = dealerSelfBuy,
                DealerSelfSell = dealerSelfSell,
                DealerSelfNet = dealerSelfNet,

                DealerHedgeBuy = dealerHedgeBuy,
                DealerHedgeSell = dealerHedgeSell,
                DealerHedgeNet = dealerHedgeNet,

                DealerTotalBuy = dealerSelfBuy + dealerHedgeBuy,
                DealerTotalSell = dealerSelfSell + dealerHedgeSell,
                DealerTotalNet = dealerSelfNet + dealerHedgeNet,

                GrandTotalNet = Num(cells, grandTotalIdx),
            };
        }

        return result;
    }
}
