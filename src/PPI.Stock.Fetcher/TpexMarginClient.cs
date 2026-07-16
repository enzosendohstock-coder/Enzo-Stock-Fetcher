using System.Text.Json;

namespace PPI.Stock.Fetcher;

/// <summary>
/// 呼叫證券櫃檯買賣中心(TPEx)「融資融券餘額表」與「信用額度總量管制餘額表」網頁背後的資料 API，
/// 合併成每支股票一筆完整的 MarginTradingDetail，涵蓋上櫃股票。
/// 支援指定任意歷史日期查詢(西元年，月/日需補零，例如 2026/01/08)，做法跟 TpexClient 相同(2026-07 逆向)。
///
/// - 融資融券：POST /www/zh-tw/margin/balance (原網頁 mainboard/trading/margin-trading/transactions.html)
///   取代舊版 tpex_mainboard_margin_balance OpenAPI(只能抓最新一天)。回傳單位是「張」，乘以 1000 換算成股，
///   跟借券賣出(已經是股)欄位單位一致，做法跟舊版一樣。
/// - 借券賣出：POST /www/zh-tw/margin/sbl (原網頁 mainboard/trading/margin-trading/sbl.html，頁面標題顯示
///   「信用額度總量管制餘額」)，取代舊版 tpex_margin_sbl OpenAPI(只能抓最新一天)。這個端點同時回傳融券欄位
///   (跟 margin/balance 重複，不使用)與借券賣出欄位(股數，不用換算)。
///
/// 欄位對應已用同一天的舊版 OpenAPI 資料逐欄比對數字驗證過。
/// </summary>
public class TpexMarginClient
{
    private const string MarginUrl = "https://www.tpex.org.tw/www/zh-tw/margin/balance";
    private const string MarginRefererUrl = "https://www.tpex.org.tw/zh-tw/mainboard/trading/margin-trading/transactions.html";

    private const string SblUrl = "https://www.tpex.org.tw/www/zh-tw/margin/sbl";
    private const string SblRefererUrl = "https://www.tpex.org.tw/zh-tw/mainboard/trading/margin-trading/sbl.html";

    private readonly HttpClient _httpClient;

    public TpexMarginClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// 取得指定日期，全市場上櫃股票的融資融券 + 借券賣出餘額明細。
    /// 若當天非交易日(假日)，回傳空字典。
    /// </summary>
    public async Task<Dictionary<string, MarginTradingDetail>> GetMarginTradingAsync(DateOnly date)
    {
        var marginTask = FetchAsync(MarginUrl, MarginRefererUrl, date);
        var sblTask = FetchAsync(SblUrl, SblRefererUrl, date);
        await Task.WhenAll(marginTask, sblTask);

        var marginRows = await marginTask;
        var sblRows = await sblTask;

        var result = new Dictionary<string, MarginTradingDetail>();
        foreach (var (code, cells) in marginRows)
        {
            sblRows.TryGetValue(code, out var sbl);

            // margin/balance 回傳單位是「張」，乘以 1000 換算成股。
            long Lots(int idx) => Num(cells, idx) * 1000;

            result[code] = new MarginTradingDetail
            {
                StockCode = code,
                StockName = cells[1].Trim(),
                Market = Market.Otc,

                MarginBalancePrev = Lots(2),
                MarginBuy = Lots(3),
                MarginSell = Lots(4),
                MarginCashRedemption = Lots(5),
                MarginBalance = Lots(6),
                MarginQuota = Lots(9),

                ShortBalancePrev = Lots(10),
                ShortSell = Lots(11),
                ShortBuy = Lots(12),
                ShortStockRedemption = Lots(13),
                ShortBalance = Lots(14),
                ShortQuota = Lots(17),

                Offsetting = Lots(18),

                // margin/sbl 回傳單位已經是股，不用換算。
                SblBalancePrev = sbl != null ? Num(sbl, 8) : 0,
                SblSell = sbl != null ? Num(sbl, 9) : 0,
                SblReturn = sbl != null ? Num(sbl, 10) : 0,
                SblAdjustment = sbl != null ? Num(sbl, 11) : 0,
                SblBalance = sbl != null ? Num(sbl, 12) : 0,
                SblQuota = sbl != null ? Num(sbl, 13) : 0,
            };
        }

        return result;
    }

    private async Task<Dictionary<string, string[]>> FetchAsync(string url, string refererUrl, DateOnly date)
    {
        var dateParam = $"{date.Year:D4}/{date.Month:D2}/{date.Day:D2}";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["date"] = dateParam,
            ["id"] = "",
            ["response"] = "json",
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.Add("Referer", refererUrl);

        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var result = new Dictionary<string, string[]>();

        var stat = root.TryGetProperty("stat", out var statEl) ? statEl.GetString() : null;
        if (stat != "ok" || !root.TryGetProperty("tables", out var tablesEl) || tablesEl.GetArrayLength() == 0)
        {
            return result;
        }

        var table = tablesEl[0];
        if (!table.TryGetProperty("data", out var dataEl) || dataEl.GetArrayLength() == 0)
        {
            // 非交易日(假日)或當天尚未公布資料，官方會回傳 stat=ok 但 data 是空陣列。
            return result;
        }

        foreach (var row in dataEl.EnumerateArray())
        {
            var cells = row.EnumerateArray().Select(c => c.GetString() ?? "").ToArray();
            if (cells.Length < 2)
            {
                continue;
            }

            var code = cells[0].Trim();
            if (string.IsNullOrEmpty(code))
            {
                continue;
            }

            result[code] = cells;
        }

        return result;
    }

    private static long Num(string[] cells, int idx) =>
        idx < cells.Length && long.TryParse(cells[idx].Trim().Replace(",", ""), out var v) ? v : 0;
}
