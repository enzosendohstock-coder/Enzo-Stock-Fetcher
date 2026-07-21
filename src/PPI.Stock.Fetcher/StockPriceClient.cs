using System.Text.Json;

namespace PPI.Stock.Fetcher;

/// <summary>
/// 呼叫 TWSE「每日收盤行情(全部)」(MI_INDEX) 與 TPEx「上櫃股票每日收盤行情」(afterTrading/otc)
/// API，取得全市場(上市+上櫃)股票的每日 OHLC 收盤行情。支援任意歷史日期查詢。
///
/// - TWSE：GET /rwd/zh/afterTrading/MI_INDEX，date 參數格式 yyyyMMdd(西元年)。回應裡有多個
///   子表格(指數、零股等)，用欄位名稱比對找出「每日收盤行情(全部)」那個子表格。
/// - TPEx：POST /www/zh-tw/afterTrading/otc，date 參數格式 yyyy/MM/dd(西元年，月日需補零)，
///   type=EW(所有證券，不含權證、牛熊證)，逆向自「上櫃股票每日收盤行情(不含定價)」網頁。
/// </summary>
public class StockPriceClient
{
    private const string TwseUrl = "https://www.twse.com.tw/rwd/zh/afterTrading/MI_INDEX";
    private const string TpexUrl = "https://www.tpex.org.tw/www/zh-tw/afterTrading/otc";
    private const string TpexRefererUrl = "https://www.tpex.org.tw/zh-tw/mainboard/trading/info/mi-pricing.html";

    private readonly HttpClient _httpClient;

    public StockPriceClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// 取得指定日期，全市場(上市+上櫃)股票的收盤行情明細。
    /// 若當天非交易日(假日)，回傳空字典。
    /// </summary>
    public async Task<Dictionary<string, StockPriceDetail>> GetStockPricesAsync(DateOnly date)
    {
        var listedTask = GetListedAsync(date);
        var otcTask = GetOtcAsync(date);
        await Task.WhenAll(listedTask, otcTask);

        var result = new Dictionary<string, StockPriceDetail>();
        foreach (var pair in await listedTask)
        {
            result[pair.Key] = pair.Value;
        }
        foreach (var pair in await otcTask)
        {
            result[pair.Key] = pair.Value;
        }
        return result;
    }

    private async Task<Dictionary<string, StockPriceDetail>> GetListedAsync(DateOnly date)
    {
        var dateStr = date.ToString("yyyyMMdd");
        // type=ALLBUT0999NOTIND：全部(不含大盤、指數、權證、牛熊證、可展延牛熊證)，排除掉這些
        // 非個股資料後才是單純的股票收盤行情；用 ALL 會連權證等衍生商品都撈進來，多出三萬多列。
        var url = $"{TwseUrl}?date={dateStr}&type=ALLBUT0999NOTIND&response=json";

        using var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var result = new Dictionary<string, StockPriceDetail>();

        var stat = root.TryGetProperty("stat", out var statEl) ? statEl.GetString() : null;
        if (stat != "OK" || !root.TryGetProperty("tables", out var tablesEl))
        {
            // 非交易日(假日)或當天尚未公布資料
            return result;
        }

        // MI_INDEX 回應包含多個子表格(指數、零股等)，用欄位名稱比對找出「每日收盤行情(全部)」，
        // 不依賴固定索引位置，避免 TWSE 哪天調整子表格順序就整個抓錯。
        JsonElement? priceTable = null;
        string[] priceFields = Array.Empty<string>();
        foreach (var table in tablesEl.EnumerateArray())
        {
            if (!table.TryGetProperty("fields", out var fieldsEl))
            {
                continue;
            }
            var fields = fieldsEl.EnumerateArray().Select(f => (f.GetString() ?? "").Trim()).ToArray();
            if (fields.Contains("證券代號") && fields.Contains("開盤價"))
            {
                priceTable = table;
                priceFields = fields;
                break;
            }
        }

        if (priceTable is not { } foundTable || !foundTable.TryGetProperty("data", out var dataEl))
        {
            return result;
        }

        int Idx(string name)
        {
            var i = Array.IndexOf(priceFields, name);
            if (i < 0)
            {
                throw new InvalidOperationException($"TWSE MI_INDEX 回應欄位格式異常，找不到「{name}」欄位。");
            }
            return i;
        }

        var codeIdx = Idx("證券代號");
        var nameIdx = Idx("證券名稱");
        var volumeIdx = Idx("成交股數");
        var transactionCountIdx = Idx("成交筆數");
        var turnoverValueIdx = Idx("成交金額");
        var openIdx = Idx("開盤價");
        var highIdx = Idx("最高價");
        var lowIdx = Idx("最低價");
        var closeIdx = Idx("收盤價");
        var changeSignIdx = Idx("漲跌(+/-)");
        var changeAmountIdx = Idx("漲跌價差");

        foreach (var row in dataEl.EnumerateArray())
        {
            var cells = row.EnumerateArray().Select(c => c.GetString() ?? "").ToArray();
            if (cells.Length < priceFields.Length)
            {
                // 偶爾會有欄位數量不足的異常列，跳過避免索引超出範圍。
                continue;
            }

            var code = cells[codeIdx].Trim();
            if (string.IsNullOrEmpty(code))
            {
                continue;
            }

            // 漲跌(+/-) 欄位是包在 HTML 裡的顏色標記(紅漲綠跌)，要跟漲跌價差(絕對值)兜起來
            // 才是有正負號的漲跌，TPEx 那邊漲跌欄位本身就已經帶正負號，不需要這道轉換。
            var changeSign = cells[changeSignIdx].Contains("color:red") ? 1
                : cells[changeSignIdx].Contains("color:green") ? -1
                : 0;
            var changeAmount = NumDouble(cells, changeAmountIdx);

            result[code] = new StockPriceDetail
            {
                StockCode = code,
                StockName = cells[nameIdx].Trim(),
                Market = Market.Listed,

                Open = NumDouble(cells, openIdx),
                High = NumDouble(cells, highIdx),
                Low = NumDouble(cells, lowIdx),
                Close = NumDouble(cells, closeIdx),
                Change = changeSign * changeAmount,

                Volume = NumLong(cells, volumeIdx),
                TransactionCount = NumLong(cells, transactionCountIdx),
                TurnoverValue = NumLong(cells, turnoverValueIdx),
            };
        }

        return result;
    }

    private async Task<Dictionary<string, StockPriceDetail>> GetOtcAsync(DateOnly date)
    {
        var dateParam = $"{date.Year:D4}/{date.Month:D2}/{date.Day:D2}";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["date"] = dateParam,
            ["type"] = "EW",
            ["id"] = "",
            ["response"] = "json",
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, TpexUrl) { Content = content };
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.Add("Referer", TpexRefererUrl);

        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var result = new Dictionary<string, StockPriceDetail>();

        var stat = root.TryGetProperty("stat", out var statEl) ? statEl.GetString() : null;
        if (stat != "ok" || !root.TryGetProperty("tables", out var tablesEl) || tablesEl.GetArrayLength() == 0)
        {
            return result;
        }

        var table = tablesEl[0];
        if (!table.TryGetProperty("data", out var dataEl) || dataEl.GetArrayLength() == 0
            || !table.TryGetProperty("fields", out var fieldsEl))
        {
            // 非交易日(假日)或當天尚未公布資料，官方會回傳 stat=ok 但 data 是空陣列。
            return result;
        }

        var fields = fieldsEl.EnumerateArray().Select(f => (f.GetString() ?? "").Trim()).ToArray();
        int Idx(string name)
        {
            var i = Array.IndexOf(fields, name);
            if (i < 0)
            {
                throw new InvalidOperationException($"TPEx afterTrading/otc 回應欄位格式異常，找不到「{name}」欄位。");
            }
            return i;
        }

        var codeIdx = Idx("代號");
        var nameIdx = Idx("名稱");
        var closeIdx = Idx("收盤");
        var changeIdx = Idx("漲跌");
        var openIdx = Idx("開盤");
        var highIdx = Idx("最高");
        var lowIdx = Idx("最低");
        var volumeIdx = Idx("成交股數");
        var turnoverValueIdx = Idx("成交金額(元)");
        var transactionCountIdx = Idx("成交筆數");

        foreach (var row in dataEl.EnumerateArray())
        {
            var cells = row.EnumerateArray().Select(c => c.GetString() ?? "").ToArray();
            if (cells.Length < fields.Length)
            {
                continue;
            }

            var code = cells[codeIdx].Trim();
            if (string.IsNullOrEmpty(code))
            {
                continue;
            }

            result[code] = new StockPriceDetail
            {
                StockCode = code,
                StockName = cells[nameIdx].Trim(),
                Market = Market.Otc,

                Open = NumDouble(cells, openIdx),
                High = NumDouble(cells, highIdx),
                Low = NumDouble(cells, lowIdx),
                Close = NumDouble(cells, closeIdx),
                Change = NumDouble(cells, changeIdx),

                Volume = NumLong(cells, volumeIdx),
                TransactionCount = NumLong(cells, transactionCountIdx),
                TurnoverValue = NumLong(cells, turnoverValueIdx),
            };
        }

        return result;
    }

    private static long NumLong(string[] cells, int idx) =>
        idx < cells.Length && long.TryParse(cells[idx].Trim().Replace(",", ""), out var v) ? v : 0;

    private static double NumDouble(string[] cells, int idx) =>
        idx < cells.Length && double.TryParse(cells[idx].Trim().Replace(",", ""), out var v) ? v : 0;
}
