using System.Text.Json;

namespace PPI.Stock.Fetcher;

/// <summary>
/// 呼叫證券櫃檯買賣中心(TPEx)「三大法人買賣明細資訊」網頁背後的資料 API，涵蓋上櫃股票。
/// 支援指定任意歷史日期查詢(西元年，月/日需補零，例如 2026/01/08)。
///
/// 這個端點是 2026-07 從 https://www.tpex.org.tw/zh-tw/mainboard/trading/major-institutional/detail/day.html
/// 這個網頁實際使用的 API 逆向出來的，跟舊版 tpex_3insti_daily_trading OpenAPI（只能抓最新一天）不同：
/// - 舊版 OpenAPI：GET /openapi/v1/tpex_3insti_daily_trading，不支援日期參數，永遠回傳最新一個交易日。
/// - 這個新端點：POST /www/zh-tw/insti/dailyTrade，body 帶 date 參數即可查任意歷史日期。
///
/// 欄位對應(共 24 欄，索引0起)已用同一天的舊版 OpenAPI 資料逐欄比對數字驗證過，關係如下：
///   0:代號 1:名稱
///   2-4:  外陸資(不含外資自營商) 買/賣/淨
///   5-7:  外資自營商 買/賣/淨
///   8-10: 外資合計 買/賣/淨（= 2-4 + 5-7）
///   11-13:投信 買/賣/淨
///   14-16:自營商-自行買賣 買/賣/淨（舊版 OpenAPI 沒有這個細分）
///   17-19:自營商-避險 買/賣/淨（舊版 OpenAPI 沒有這個細分）
///   20-22:自營商合計 買/賣/淨（= 14-16 + 17-19，對應舊版 OpenAPI 的 Dealers-*）
///   23:   三大法人合計買賣超(淨額)
/// </summary>
public class TpexClient
{
    private const string Url = "https://www.tpex.org.tw/www/zh-tw/insti/dailyTrade";
    private const string RefererUrl = "https://www.tpex.org.tw/zh-tw/mainboard/trading/major-institutional/detail/day.html";

    private readonly HttpClient _httpClient;

    public TpexClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// 取得指定日期，全市場上櫃股票的三大法人買賣超明細。
    /// 若當天非交易日(假日)，回傳空字典。
    /// </summary>
    public async Task<Dictionary<string, InstitutionalTradeDetail>> GetInstitutionalTradesAsync(DateOnly date)
    {
        var dateParam = $"{date.Year:D4}/{date.Month:D2}/{date.Day:D2}";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["type"] = "Daily",
            ["sect"] = "AL",
            ["date"] = dateParam,
            ["id"] = "",
            ["response"] = "json",
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, Url) { Content = content };
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.Add("Referer", RefererUrl);

        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var stat = root.TryGetProperty("stat", out var statEl) ? statEl.GetString() : null;
        if (stat != "ok" || !root.TryGetProperty("tables", out var tablesEl) || tablesEl.GetArrayLength() == 0)
        {
            return new Dictionary<string, InstitutionalTradeDetail>();
        }

        var table = tablesEl[0];
        if (!table.TryGetProperty("data", out var dataEl) || dataEl.GetArrayLength() == 0)
        {
            // 非交易日(假日)或當天尚未公布資料，官方會回傳 stat=ok 但 data 是空陣列。
            return new Dictionary<string, InstitutionalTradeDetail>();
        }

        long Num(string[] cells, int idx) =>
            idx < cells.Length && long.TryParse(cells[idx].Trim().Replace(",", ""), out var v) ? v : 0;

        var result = new Dictionary<string, InstitutionalTradeDetail>();
        foreach (var row in dataEl.EnumerateArray())
        {
            var cells = row.EnumerateArray().Select(c => c.GetString() ?? "").ToArray();
            if (cells.Length < 24)
            {
                // 偶爾會有欄位數量不足的異常列，跳過避免索引超出範圍。
                continue;
            }

            var code = cells[0].Trim();
            if (string.IsNullOrEmpty(code))
            {
                continue;
            }

            var dealerSelfBuy = Num(cells, 14);
            var dealerSelfSell = Num(cells, 15);
            var dealerSelfNet = Num(cells, 16);
            var dealerHedgeBuy = Num(cells, 17);
            var dealerHedgeSell = Num(cells, 18);
            var dealerHedgeNet = Num(cells, 19);

            result[code] = new InstitutionalTradeDetail
            {
                StockCode = code,
                StockName = cells[1].Trim(),
                Market = Market.Otc,

                ForeignExDealerBuy = Num(cells, 2),
                ForeignExDealerSell = Num(cells, 3),
                ForeignExDealerNet = Num(cells, 4),

                ForeignDealerBuy = Num(cells, 5),
                ForeignDealerSell = Num(cells, 6),
                ForeignDealerNet = Num(cells, 7),

                ForeignTotalBuy = Num(cells, 8),
                ForeignTotalSell = Num(cells, 9),
                ForeignTotalNet = Num(cells, 10),

                TrustBuy = Num(cells, 11),
                TrustSell = Num(cells, 12),
                TrustNet = Num(cells, 13),

                DealerSelfBuy = dealerSelfBuy,
                DealerSelfSell = dealerSelfSell,
                DealerSelfNet = dealerSelfNet,

                DealerHedgeBuy = dealerHedgeBuy,
                DealerHedgeSell = dealerHedgeSell,
                DealerHedgeNet = dealerHedgeNet,

                DealerTotalBuy = Num(cells, 20),
                DealerTotalSell = Num(cells, 21),
                DealerTotalNet = Num(cells, 22),

                GrandTotalNet = Num(cells, 23),
            };
        }

        return result;
    }
}
