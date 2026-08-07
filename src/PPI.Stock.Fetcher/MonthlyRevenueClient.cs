using System.Text.Json;

namespace PPI.Stock.Fetcher;

/// <summary>
/// 呼叫 TWSE/TPEx OpenAPI 的「上市/上櫃公司每月營業收入彙總表」，只回傳「目前最新一期」資料——
/// 這兩個官方 opendata 端點本質上是「目前為止已申報公司的最新月營收快照」，無法查詢過去月份
/// (歷史回補要另外一套逐公司查詢的機制，這次先不做，等這個管線先穩定運作一陣子再說)。
/// 上月比較增減%、去年同月增減%、累計前期比較增減% 都是官方算好的欄位，直接映射，不用自己算。
///
/// - TWSE：GET /v1/opendata/t187ap05_L，不需要特殊 header。
/// - TPEx：GET /openapi/v1/mopsfin_t187ap05_O，欄位名稱跟 TWSE 完全一致(只是網域不同)，
///   但需要帶一般瀏覽器 UA + Referer 才不會被擋(單純用 HttpClient 預設值會是空的/被拒絕)。
/// 兩邊欄位結構相同，用同一套 ParseRows 解析，只有 URL/Market/是否需要 TPEx headers 不同，
/// 沿用 ForeignShareholdingClient.cs 的合併寫法而不是拆成兩個檔案。
/// </summary>
public class MonthlyRevenueClient
{
    private const string TwseUrl = "https://openapi.twse.com.tw/v1/opendata/t187ap05_L";
    private const string TpexUrl = "https://www.tpex.org.tw/openapi/v1/mopsfin_t187ap05_O";
    private const string TpexRefererUrl = "https://www.tpex.org.tw/";
    private const string TpexUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";

    private readonly HttpClient _httpClient;

    public MonthlyRevenueClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// 取得目前最新一期(通常是上個月)全市場(上市+上櫃)月營收，回傳值額外帶出這一期的資料年月
    /// (轉成西元年月的第一天，方便跟其他表比較)。上市、上櫃理論上該是同一個月，
    /// 但還是各自讀出實際回傳值，兩邊真的兜不起來時以上市為準並印警告，方便之後排查。
    /// </summary>
    public async Task<(DateOnly YearMonth, Dictionary<string, MonthlyRevenueDetail> Details)> GetMonthlyRevenueAsync()
    {
        var listedTask = GetAsync(TwseUrl, Market.Listed, useTpexHeaders: false);
        var otcTask = GetAsync(TpexUrl, Market.Otc, useTpexHeaders: true);
        await Task.WhenAll(listedTask, otcTask);

        var (listedYearMonth, listed) = await listedTask;
        var (otcYearMonth, otc) = await otcTask;

        if (listedYearMonth.HasValue && otcYearMonth.HasValue && listedYearMonth != otcYearMonth)
        {
            Console.WriteLine($"警告：月營收上市/上櫃回傳的資料年月不一致(上市={listedYearMonth:yyyy-MM}，上櫃={otcYearMonth:yyyy-MM})，以上市為準。");
        }

        var yearMonth = listedYearMonth ?? otcYearMonth
            ?? throw new InvalidOperationException("月營收上市、上櫃回應都找不到「資料年月」欄位，格式可能已變更。");

        var result = new Dictionary<string, MonthlyRevenueDetail>();
        foreach (var pair in listed)
        {
            result[pair.Key] = pair.Value;
        }
        foreach (var pair in otc)
        {
            result[pair.Key] = pair.Value;
        }
        return (yearMonth, result);
    }

    private async Task<(DateOnly? YearMonth, Dictionary<string, MonthlyRevenueDetail> Details)> GetAsync(
        string url, Market market, bool useTpexHeaders)
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
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Array)
        {
            return (null, new Dictionary<string, MonthlyRevenueDetail>());
        }

        // 官方欄位值目前一律是 JSON 字串，但保險起見用 GetRawText 相容萬一哪天回傳成數字型別。
        static string S(JsonElement row, string name) => row.TryGetProperty(name, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()).Trim()
            : "";

        static long NumLong(JsonElement row, string name)
        {
            var s = S(row, name).Replace(",", "");
            return long.TryParse(s, out var v) ? v : 0;
        }

        // 比較基期為 0 時(例如新股剛掛牌)，官方這幾個%欄位會留白，對應保留成 null 而不是硬塞 0。
        static double? NumDoubleOrNull(JsonElement row, string name)
        {
            var s = S(row, name);
            return string.IsNullOrEmpty(s) ? null : double.TryParse(s, out var v) ? v : null;
        }

        DateOnly? yearMonth = null;
        var result = new Dictionary<string, MonthlyRevenueDetail>();

        foreach (var row in root.EnumerateArray())
        {
            var code = S(row, "公司代號");
            if (string.IsNullOrEmpty(code))
            {
                continue;
            }

            if (yearMonth == null)
            {
                // 資料年月是民國年月(例如 11506 = 115年06月)，轉西元年月的第一天。
                var raw = S(row, "資料年月");
                if (raw.Length == 5 && int.TryParse(raw[..3], out var rocYear) && int.TryParse(raw[3..], out var month))
                {
                    yearMonth = new DateOnly(rocYear + 1911, month, 1);
                }
            }

            result[code] = new MonthlyRevenueDetail
            {
                StockCode = code,
                StockName = S(row, "公司名稱"),
                Market = market,
                Industry = S(row, "產業別"),
                Revenue = NumLong(row, "營業收入-當月營收"),
                RevenuePrevMonth = NumLong(row, "營業收入-上月營收"),
                RevenueLastYearMonth = NumLong(row, "營業收入-去年當月營收"),
                MomPercent = NumDoubleOrNull(row, "營業收入-上月比較增減(%)"),
                YoyPercent = NumDoubleOrNull(row, "營業收入-去年同月增減(%)"),
                CumulativeRevenue = NumLong(row, "累計營業收入-當月累計營收"),
                CumulativeRevenueLastYear = NumLong(row, "累計營業收入-去年累計營收"),
                CumulativeYoyPercent = NumDoubleOrNull(row, "累計營業收入-前期比較增減(%)"),
            };
        }

        return (yearMonth, result);
    }
}
