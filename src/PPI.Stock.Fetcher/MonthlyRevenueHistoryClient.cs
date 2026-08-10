using System.Text.RegularExpressions;

namespace PPI.Stock.Fetcher;

/// <summary>
/// 呼叫 MOPS 舊式的「個股單月營收查詢」頁面(mopsov.twse.com.tw)，逐公司、逐月查詢歷史月營收——
/// 這是目前唯一能拿到「非最新一期」月營收資料的管道：TWSE OpenAPI(t187ap05_L) 跟政府資料開放平台
/// 都只提供「目前最新一期」的快照，沒有整批的歷史封存版本，舊式的整月批次下載頁面(t21sc03系列)
/// 也已經確認下架了。上市、上櫃股票共用同一個查詢端點，不用分開處理。
///
/// 這個頁面沒有「上月比較(MoM%)」這個比較基準，只有跟去年同期比、跟去年累計比，
/// MoM% 要由呼叫端傳入「上個月營收」自己算(通常是逐月往前查的時候，上個月的營收前一次呼叫已經拿到了)。
///
/// HTML 裡「增減金額」「增減百分比」這兩個標籤各出現兩次(一次是跟去年同期比、一次是跟去年累計比)，
/// 沒辦法用標籤名稱查找，改用固定順序讀取這 8 個數值儲存格(都是 style='text-align:right !important'
/// 這個樣式，備註欄是 text-align:left，天然不會被這個正規表示式吃到)：
///   0:本月營收 1:去年同期營收 2:YoY增減金額(不用) 3:YoY增減百分比
///   4:本年累計營收 5:去年累計營收 6:累計增減金額(不用) 7:累計增減百分比
/// </summary>
public class MonthlyRevenueHistoryClient
{
    private const string UrlTemplate =
        "https://mopsov.twse.com.tw/mops/web/ajax_t05st10_ifrs?firstin=true&off=1&step=0&co_id={0}&year={1}&month={2}&yearmonth={1}{2}";

    private static readonly Regex ValueCellPattern = new(
        @"<TD class='(?:odd|even)' style='text-align:right !important;'>&nbsp;\s*([-\d,\.]*)</TD>",
        RegexOptions.Compiled);

    private readonly HttpClient _httpClient;

    public MonthlyRevenueHistoryClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// 查詢指定股票、指定西元年月的月營收。查無資料(該公司這個月份還沒掛牌、或該期尚未申報)回傳 null，
    /// 呼叫端應視為「回補到此為止」的訊號而不是錯誤。
    /// previousMonthRevenue 是上個月的營收金額(千元)，用來現算 MoM%；第一次呼叫(沒有更早資料)傳 null。
    /// </summary>
    public async Task<MonthlyRevenueDetail?> GetMonthlyRevenueAsync(
        string stockCode, string stockName, Market market, DateOnly yearMonth, long? previousMonthRevenue)
    {
        var rocYear = yearMonth.Year - 1911;
        var url = string.Format(UrlTemplate, stockCode, rocYear, yearMonth.Month.ToString("D2"));

        using var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        var matches = ValueCellPattern.Matches(html);
        if (matches.Count < 8)
        {
            return null;
        }

        double? NumOrNull(int idx)
        {
            var s = matches[idx].Groups[1].Value.Replace(",", "").Trim();
            return string.IsNullOrEmpty(s) ? null : double.TryParse(s, out var v) ? v : null;
        }

        var revenueRaw = NumOrNull(0);
        if (revenueRaw == null)
        {
            return null;
        }

        var revenue = (long)revenueRaw.Value;
        var momPercent = previousMonthRevenue is > 0
            ? (revenue - previousMonthRevenue.Value) * 100.0 / previousMonthRevenue.Value
            : (double?)null;

        return new MonthlyRevenueDetail
        {
            StockCode = stockCode,
            StockName = stockName,
            Market = market,
            Industry = "", // 這個查詢頁面沒有產業別欄位，歷史回補的資料這欄留空
            Revenue = revenue,
            RevenuePrevMonth = previousMonthRevenue ?? 0,
            RevenueLastYearMonth = (long)(NumOrNull(1) ?? 0),
            MomPercent = momPercent,
            YoyPercent = NumOrNull(3),
            CumulativeRevenue = (long)(NumOrNull(4) ?? 0),
            CumulativeRevenueLastYear = (long)(NumOrNull(5) ?? 0),
            CumulativeYoyPercent = NumOrNull(7),
        };
    }
}
