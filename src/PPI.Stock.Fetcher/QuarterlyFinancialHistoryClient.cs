using System.Text.RegularExpressions;

namespace PPI.Stock.Fetcher;

/// <summary>
/// 呼叫 MOPS 的季報彙總查詢頁面(mopsov.twse.com.tw)，一次查詢「整個市場」某一季的資料——
/// 跟月營收的歷史回補(MonthlyRevenueHistoryClient)不同，這裡不是逐股票查，是逐季查，
/// 一次請求就能拿到該市場所有公司這一季的資料，效率高很多，不需要逐股票的回補設計。
///
/// 兩個報表要分開打：
/// - ajax_t163sb06：毛利率/營業利益率/稅前純益率/稅後純益率四率，單一表格，官方本身只涵蓋
///   一般業(跟例行抓取的 t187ap17_L 範圍一致)。
/// - ajax_t163sb04：損益表金額+EPS，同一個回應裡把金控/保險/一般業...等產業別的表格串在一起，
///   這裡只解析「一般業」那個子表格——用「每列固定 30 個儲存格」辨識(代號+名稱+28個金額欄位)，
///   其餘產業別損益表科目完全不同、欄位數不同(實測金控業是22格)，不會誤判成一般業。
///   只有一般業會有完整金額欄位，跟例行抓取(QuarterlyFinancialClient.cs)的欄位範圍限制一致。
///
/// 跟月營收歷史回補同樣的 2013 年門檻：這兩個報表都是 IFRS 格式，2013 年以前(民國102年以前)
/// MOPS 回傳的是純前端 JS 轉址頁面(轉去舊版無 IFRS 後綴的報表)，裡面沒有任何資料列可以解析，
/// 自然會回傳空字典，呼叫端視為「查無資料」即可，不需要額外偵測轉址頁面。
/// </summary>
public class QuarterlyFinancialHistoryClient
{
    private const string RatioUrl = "https://mopsov.twse.com.tw/mops/web/ajax_t163sb06";
    private const string IncomeUrl = "https://mopsov.twse.com.tw/mops/web/ajax_t163sb04";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";

    private readonly HttpClient _httpClient;

    public QuarterlyFinancialHistoryClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// 查詢指定市場、指定西元年季的季報彙總(四率+損益表，僅一般業)。查無資料(該季還沒到申報期、
    /// 或已經回溯到 2013 年之前)回傳空字典，呼叫端應視為「回補到此為止」的訊號而不是錯誤。
    /// </summary>
    public async Task<Dictionary<string, QuarterlyFinancialDetail>> GetQuarterAsync(Market market, int year, int quarter)
    {
        var typek = market == Market.Listed ? "sii" : "otc";
        var rocYear = year - 1911;

        var ratioTask = PostFormAsync(RatioUrl, typek, rocYear, quarter);
        var incomeTask = PostFormAsync(IncomeUrl, typek, rocYear, quarter);
        await Task.WhenAll(ratioTask, incomeTask);

        var ratios = ParseRatioTable(await ratioTask);
        var income = ParseGeneralIndustryIncomeTable(await incomeTask);

        // 用損益表的代號清單為主(涵蓋全部產業別)，四率官方本身就只有一般業才有，這裡只是
        // 剛好都拿一般業的損益表去配對；四率查不到(理論上不該發生，一般業本來就都在損益表裡)
        // 就讓四個%留 null，不強行湊出不完整的資料。
        var result = new Dictionary<string, QuarterlyFinancialDetail>();
        foreach (var (code, inc) in income)
        {
            ratios.TryGetValue(code, out var ratio);
            result[code] = new QuarterlyFinancialDetail
            {
                StockCode = code,
                StockName = inc.StockName,
                Market = market,
                IndustryCategory = "ci",
                Revenue = inc.Revenue,
                CostOfRevenue = inc.CostOfRevenue,
                GrossProfit = inc.GrossProfit,
                OperatingExpenses = inc.OperatingExpenses,
                OperatingIncome = inc.OperatingIncome,
                NonOperatingIncomeExpenses = inc.NonOperatingIncomeExpenses,
                PretaxIncome = inc.PretaxIncome,
                IncomeTaxExpense = inc.IncomeTaxExpense,
                NetIncome = inc.NetIncome,
                Eps = inc.Eps,
                GrossMargin = ratio.GrossMargin,
                OperatingMargin = ratio.OperatingMargin,
                PretaxMargin = ratio.PretaxMargin,
                NetMargin = ratio.NetMargin,
            };
        }
        return result;
    }

    private async Task<string> PostFormAsync(string url, string typek, int rocYear, int quarter)
    {
        var form = new Dictionary<string, string>
        {
            ["encodeURIComponent"] = "1",
            ["step"] = "1",
            ["firstin"] = "1",
            ["off"] = "1",
            ["isQuery"] = "Y",
            ["TYPEK"] = typek,
            ["year"] = rocYear.ToString(),
            ["season"] = quarter.ToString("D2"),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        request.Headers.UserAgent.ParseAdd(UserAgent);

        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static readonly Regex DataRowPattern = new(@"<tr class='(?:odd|even)'>((?:<td[^>]*>[^<]*</td>)+)</tr>", RegexOptions.Compiled);
    private static readonly Regex CellPattern = new(@"<td[^>]*>([^<]*)</td>", RegexOptions.Compiled);

    private readonly record struct RatioRow(double? GrossMargin, double? OperatingMargin, double? PretaxMargin, double? NetMargin);

    // 四率表格每列固定 7 個儲存格：代號、名稱、營業收入(百萬元，不用)、毛利率、營業利益率、
    // 稅前純益率、稅後純益率。單一表格，不用像損益表那樣分辨產業別。
    private static Dictionary<string, RatioRow> ParseRatioTable(string html)
    {
        var result = new Dictionary<string, RatioRow>();
        foreach (Match rowMatch in DataRowPattern.Matches(html))
        {
            var cells = CellPattern.Matches(rowMatch.Groups[1].Value);
            if (cells.Count != 7)
            {
                continue;
            }

            var code = cells[0].Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(code))
            {
                continue;
            }

            result[code] = new RatioRow(
                NumOrNull(cells[3].Groups[1].Value),
                NumOrNull(cells[4].Groups[1].Value),
                NumOrNull(cells[5].Groups[1].Value),
                NumOrNull(cells[6].Groups[1].Value));
        }
        return result;
    }

    private readonly record struct IncomeRow(
        string StockName, long? Revenue, long? CostOfRevenue, long? GrossProfit, long? OperatingExpenses,
        long? OperatingIncome, long? NonOperatingIncomeExpenses, long? PretaxIncome, long? IncomeTaxExpense,
        long? NetIncome, double? Eps);

    // 損益表回應把金控/保險/一般業...等產業別的表格串在同一頁，用「儲存格數量」辨識哪一列屬於
    // 一般業：實測一般業每列固定 30 個 <td>(代號+名稱+28個金額/比率欄位)，其餘產業別損益表科目
    // 完全不同、欄位數不同(金控業實測是22格)，不會誤判。以下 28 個欄位由左到右的固定位置對照
    // (index 0 = 緊接在代號/名稱後的第一個金額欄位)：
    //   0:營業收入 1:營業成本 2:原始認列生物資產損益 3:生物資產公允價值變動損益 4:營業毛利（毛損）
    //   5:未實現銷貨損益 6:已實現銷貨損益 7:營業毛利（毛損）淨額 8:營業費用 9:其他收益及費損淨額
    //   10:營業利益（損失） 11:營業外收入及支出 12:稅前淨利（淨損） 13:所得稅費用（利益）
    //   14:繼續營業單位本期淨利 15:停業單位損益 16:合併前非屬共同控制股權損益 17:本期淨利（淨損）
    //   18~26:其他綜合損益/歸屬明細(不用) 27:基本每股盈餘（元）
    // 官方改版才需要跟著調整這些 index。
    private static Dictionary<string, IncomeRow> ParseGeneralIndustryIncomeTable(string html)
    {
        var result = new Dictionary<string, IncomeRow>();
        foreach (Match rowMatch in DataRowPattern.Matches(html))
        {
            var cells = CellPattern.Matches(rowMatch.Groups[1].Value);
            if (cells.Count != 30)
            {
                continue;
            }

            var code = cells[0].Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(code) || !code.All(char.IsDigit))
            {
                continue;
            }

            string Cell(int dataIndex) => cells[2 + dataIndex].Groups[1].Value;

            result[code] = new IncomeRow(
                StockName: cells[1].Groups[1].Value.Trim(),
                Revenue: NumLongOrNull(Cell(0)),
                CostOfRevenue: NumLongOrNull(Cell(1)),
                GrossProfit: NumLongOrNull(Cell(7)),
                OperatingExpenses: NumLongOrNull(Cell(8)),
                OperatingIncome: NumLongOrNull(Cell(10)),
                NonOperatingIncomeExpenses: NumLongOrNull(Cell(11)),
                PretaxIncome: NumLongOrNull(Cell(12)),
                IncomeTaxExpense: NumLongOrNull(Cell(13)),
                NetIncome: NumLongOrNull(Cell(17)),
                Eps: NumOrNull(Cell(27)));
        }
        return result;
    }

    // 金額欄位查無資料時官方顯示 "--"，不是空字串，要一起當作 null 處理。
    private static double? NumOrNull(string raw)
    {
        var s = raw.Replace(",", "").Trim();
        return string.IsNullOrEmpty(s) || s == "--" ? null : double.TryParse(s, out var v) ? v : null;
    }

    private static long? NumLongOrNull(string raw)
    {
        var v = NumOrNull(raw);
        return v.HasValue ? (long)Math.Round(v.Value) : null;
    }
}
