using System.Text.Json;

namespace PPI.Stock.Fetcher;

/// <summary>
/// 呼叫 TWSE/TPEx OpenAPI 的「營益分析彙總表」(毛利率/營業利益率/稅前稅後純益率) +
/// 「綜合損益表」(EPS + 損益金額，依產業別分 5 個變體：一般業/證期業/金控業/保險業/異業)，
/// 合併成單一季報明細。只回傳「目前最新一期」資料，歷史回補這次先不做(同月營收)。
///
/// 損益表 5 個產業別變體的科目名稱差異很大(例如金控業用「利息淨收益」，沒有「營業成本/毛利」這種
/// 概念)，這裡只對一般業(ci)變體做完整金額欄位映射；其餘 4 個變體只抓得到「基本每股盈餘」這個
/// 唯一在 5 個變體裡都存在、意義又一致的欄位，其餘金額欄位保留 null——不是抓漏，
/// 詳見 QuarterlyFinancialDetail.cs 的類別註解。「營益分析彙總表」官方目前也只涵蓋一般業，
/// 其餘產業別的四個%同樣會是 null。
///
/// - TWSE：GET /v1/opendata/t187ap17_L(四率) + t187ap06_L_{ci,bd,fh,ins,mim}(EPS+金額)。
/// - TPEx：GET /openapi/v1/mopsfin_187ap17_O(四率，注意沒有 t 前綴，官方命名不規則) +
///   mopsfin_t187ap06_O_{ci,bd,fh,ins,mim}(EPS+金額)。
/// 兩邊財務科目欄位名稱(營業收入/營業成本/...)剛好完全一致，但「識別欄位」(代號/名稱/年度/季別)
/// 命名各不相同，甚至 TPEx 自己兩個端點都不一致(187ap17_O 用「季別」、t187ap06_O_* 卻用
/// 「Season」)，所以識別欄位名稱一律用參數傳入，不能假設一致。TPEx 都需要帶一般瀏覽器
/// UA + Referer 才不會被擋。
/// </summary>
public class QuarterlyFinancialClient
{
    private static readonly string[] IndustryVariants = { "ci", "bd", "fh", "ins", "mim" };

    private const string TwseRatioUrl = "https://openapi.twse.com.tw/v1/opendata/t187ap17_L";
    private const string TwseIncomeUrlTemplate = "https://openapi.twse.com.tw/v1/opendata/t187ap06_L_{0}";
    private const string TpexRatioUrl = "https://www.tpex.org.tw/openapi/v1/mopsfin_187ap17_O";
    private const string TpexIncomeUrlTemplate = "https://www.tpex.org.tw/openapi/v1/mopsfin_t187ap06_O_{0}";
    private const string TpexRefererUrl = "https://www.tpex.org.tw/";
    private const string TpexUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";

    private readonly HttpClient _httpClient;

    public QuarterlyFinancialClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// 取得目前最新一期(通常是上一季)全市場(上市+上櫃)季報四率 + EPS + 損益金額。
    /// </summary>
    public async Task<(int Year, int Quarter, Dictionary<string, QuarterlyFinancialDetail> Details)> GetQuarterlyFinancialsAsync()
    {
        var listedTask = GetMarketAsync(Market.Listed);
        var otcTask = GetMarketAsync(Market.Otc);
        await Task.WhenAll(listedTask, otcTask);

        var (listedPeriod, listed) = await listedTask;
        var (otcPeriod, otc) = await otcTask;

        if (listedPeriod.HasValue && otcPeriod.HasValue && listedPeriod != otcPeriod)
        {
            Console.WriteLine($"警告：季報上市/上櫃回傳的年季不一致(上市={listedPeriod}，上櫃={otcPeriod})，以上市為準。");
        }

        var period = listedPeriod ?? otcPeriod
            ?? throw new InvalidOperationException("季報上市、上櫃回應都找不到「年度/季別」欄位，格式可能已變更。");

        var result = new Dictionary<string, QuarterlyFinancialDetail>();
        foreach (var pair in listed)
        {
            result[pair.Key] = pair.Value;
        }
        foreach (var pair in otc)
        {
            result[pair.Key] = pair.Value;
        }
        return (period.Year, period.Quarter, result);
    }

    private async Task<((int Year, int Quarter)? Period, Dictionary<string, QuarterlyFinancialDetail> Details)> GetMarketAsync(Market market)
    {
        var isTpex = market == Market.Otc;

        var (ratioPeriod, ratios) = isTpex
            ? await FetchRatiosAsync(TpexRatioUrl, useTpexHeaders: true,
                codeField: "SecuritiesCompanyCode", yearField: "Year", quarterField: "季別",
                grossField: "毛利率", operatingField: "營業利益率", pretaxField: "稅前純益率", netField: "稅後純益率")
            : await FetchRatiosAsync(TwseRatioUrl, useTpexHeaders: false,
                codeField: "公司代號", yearField: "年度", quarterField: "季別",
                grossField: "毛利率(%)(營業毛利)/(營業收入)", operatingField: "營業利益率(%)(營業利益)/(營業收入)",
                pretaxField: "稅前純益率(%)(稅前純益)/(營業收入)", netField: "稅後純益率(%)(稅後純益)/(營業收入)");

        (int Year, int Quarter)? period = ratioPeriod;
        var merged = new Dictionary<string, QuarterlyFinancialDetail>();

        foreach (var variant in IndustryVariants)
        {
            var url = string.Format(isTpex ? TpexIncomeUrlTemplate : TwseIncomeUrlTemplate, variant);
            var (incomePeriod, incomeRows) = isTpex
                ? await FetchIncomeStatementAsync(url, useTpexHeaders: true,
                    codeField: "SecuritiesCompanyCode", nameField: "CompanyName", yearField: "Year", quarterField: "Season",
                    fullAmounts: variant == "ci")
                : await FetchIncomeStatementAsync(url, useTpexHeaders: false,
                    codeField: "公司代號", nameField: "公司名稱", yearField: "年度", quarterField: "季別",
                    fullAmounts: variant == "ci");

            period ??= incomePeriod;

            foreach (var (code, income) in incomeRows)
            {
                ratios.TryGetValue(code, out var ratio);
                merged[code] = new QuarterlyFinancialDetail
                {
                    StockCode = code,
                    StockName = income.StockName,
                    Market = market,
                    IndustryCategory = variant,
                    Revenue = income.Revenue,
                    CostOfRevenue = income.CostOfRevenue,
                    GrossProfit = income.GrossProfit,
                    OperatingExpenses = income.OperatingExpenses,
                    OperatingIncome = income.OperatingIncome,
                    NonOperatingIncomeExpenses = income.NonOperatingIncomeExpenses,
                    PretaxIncome = income.PretaxIncome,
                    IncomeTaxExpense = income.IncomeTaxExpense,
                    NetIncome = income.NetIncome,
                    Eps = income.Eps,
                    GrossMargin = ratio.GrossMargin,
                    OperatingMargin = ratio.OperatingMargin,
                    PretaxMargin = ratio.PretaxMargin,
                    NetMargin = ratio.NetMargin,
                };
            }
        }

        return (period, merged);
    }

    private readonly record struct RatioRow(double? GrossMargin, double? OperatingMargin, double? PretaxMargin, double? NetMargin);

    private readonly record struct IncomeRow(
        string StockName, long? Revenue, long? CostOfRevenue, long? GrossProfit, long? OperatingExpenses,
        long? OperatingIncome, long? NonOperatingIncomeExpenses, long? PretaxIncome, long? IncomeTaxExpense,
        long? NetIncome, double? Eps);

    private async Task<((int Year, int Quarter)? Period, Dictionary<string, RatioRow> Rows)> FetchRatiosAsync(
        string url, bool useTpexHeaders, string codeField, string yearField, string quarterField,
        string grossField, string operatingField, string pretaxField, string netField)
    {
        var root = await GetJsonArrayAsync(url, useTpexHeaders);
        (int Year, int Quarter)? period = null;
        var result = new Dictionary<string, RatioRow>();

        foreach (var row in root.EnumerateArray())
        {
            var code = S(row, codeField);
            if (string.IsNullOrEmpty(code))
            {
                continue;
            }

            period ??= ParseRocYearQuarter(S(row, yearField), S(row, quarterField));

            result[code] = new RatioRow(
                NumDoubleOrNull(row, grossField),
                NumDoubleOrNull(row, operatingField),
                NumDoubleOrNull(row, pretaxField),
                NumDoubleOrNull(row, netField));
        }

        return (period, result);
    }

    private async Task<((int Year, int Quarter)? Period, Dictionary<string, IncomeRow> Rows)> FetchIncomeStatementAsync(
        string url, bool useTpexHeaders, string codeField, string nameField, string yearField, string quarterField, bool fullAmounts)
    {
        var root = await GetJsonArrayAsync(url, useTpexHeaders);
        (int Year, int Quarter)? period = null;
        var result = new Dictionary<string, IncomeRow>();

        foreach (var row in root.EnumerateArray())
        {
            var code = S(row, codeField);
            if (string.IsNullOrEmpty(code))
            {
                continue;
            }

            period ??= ParseRocYearQuarter(S(row, yearField), S(row, quarterField));

            result[code] = new IncomeRow(
                StockName: S(row, nameField),
                Revenue: fullAmounts ? NumLongOrNull(row, "營業收入") : null,
                CostOfRevenue: fullAmounts ? NumLongOrNull(row, "營業成本") : null,
                GrossProfit: fullAmounts ? NumLongOrNull(row, "營業毛利（毛損）淨額") : null,
                OperatingExpenses: fullAmounts ? NumLongOrNull(row, "營業費用") : null,
                OperatingIncome: fullAmounts ? NumLongOrNull(row, "營業利益（損失）") : null,
                NonOperatingIncomeExpenses: fullAmounts ? NumLongOrNull(row, "營業外收入及支出") : null,
                PretaxIncome: fullAmounts ? NumLongOrNull(row, "稅前淨利（淨損）") : null,
                IncomeTaxExpense: fullAmounts ? NumLongOrNull(row, "所得稅費用（利益）") : null,
                NetIncome: fullAmounts ? NumLongOrNull(row, "本期淨利（淨損）") : null,
                Eps: NumDoubleOrNull(row, "基本每股盈餘（元）"));
        }

        return (period, result);
    }

    private async Task<JsonElement> GetJsonArrayAsync(string url, bool useTpexHeaders)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (useTpexHeaders)
        {
            request.Headers.UserAgent.ParseAdd(TpexUserAgent);
            request.Headers.Referrer = new Uri(TpexRefererUrl);
        }

        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement.Clone();
        return root.ValueKind == JsonValueKind.Array ? root : JsonDocument.Parse("[]").RootElement;
    }

    // 資料年月/年季用民國年，轉西元年份；沒有資料(例如這個變體剛好目前沒有任何公司)時回傳 null。
    private static (int Year, int Quarter)? ParseRocYearQuarter(string rocYearStr, string quarterStr)
    {
        if (int.TryParse(rocYearStr, out var rocYear) && int.TryParse(quarterStr, out var quarter))
        {
            return (rocYear + 1911, quarter);
        }
        return null;
    }

    // 官方欄位值目前一律是 JSON 字串，但保險起見用 GetRawText 相容萬一哪天回傳成數字型別。
    private static string S(JsonElement row, string name) => row.TryGetProperty(name, out var v)
        ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()).Trim()
        : "";

    // 損益表金額欄位官方是用「14401643.00」這種帶小數點的字串格式(千元，但一律是整數值只是格式化成
    // 兩位小數)，不能直接 long.TryParse(會失敗)，要先當 double 解析再四捨五入回 long。
    private static long? NumLongOrNull(JsonElement row, string name)
    {
        var s = S(row, name).Replace(",", "");
        return string.IsNullOrEmpty(s) ? null : double.TryParse(s, out var v) ? (long)Math.Round(v) : null;
    }

    private static double? NumDoubleOrNull(JsonElement row, string name)
    {
        var s = S(row, name);
        return string.IsNullOrEmpty(s) ? null : double.TryParse(s, out var v) ? v : null;
    }
}
