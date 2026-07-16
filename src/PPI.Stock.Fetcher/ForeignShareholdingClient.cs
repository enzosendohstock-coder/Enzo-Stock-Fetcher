using System.Text.Json;

namespace PPI.Stock.Fetcher;

/// <summary>
/// 呼叫 TWSE「外資及陸資投資持股統計」(MI_QFIIS) 與 TPEx「僑外資及陸資持股比例排行表」
/// (insti/qfii) API，取得全市場(上市+上櫃)股票的外資及陸資持股比率。
/// 兩邊都是集保結算所登記的官方每日公告數字，支援任意歷史日期查詢。
///
/// - TWSE：GET /rwd/zh/fund/MI_QFIIS，date 參數格式 yyyyMMdd(西元年)。
/// - TPEx：POST /www/zh-tw/insti/qfii，date 參數格式 yyyy/MM/dd(西元年，月日需補零)，
///   做法跟三大法人買賣超的 TpexClient 一樣是從網頁背後的 API 逆向出來的。
/// </summary>
public class ForeignShareholdingClient
{
    private const string TwseUrl = "https://www.twse.com.tw/rwd/zh/fund/MI_QFIIS";
    private const string TpexUrl = "https://www.tpex.org.tw/www/zh-tw/insti/qfii";
    private const string TpexRefererUrl = "https://www.tpex.org.tw/zh-tw/mainboard/trading/major-institutional/stock-ocfi.html";

    private readonly HttpClient _httpClient;

    public ForeignShareholdingClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// 取得指定日期，全市場(上市+上櫃)股票的外資及陸資持股比率明細。
    /// 若當天非交易日(假日)，回傳空字典。
    /// </summary>
    public async Task<Dictionary<string, ForeignShareholdingDetail>> GetForeignShareholdingAsync(DateOnly date)
    {
        var listedTask = GetListedAsync(date);
        var otcTask = GetOtcAsync(date);
        await Task.WhenAll(listedTask, otcTask);

        var result = new Dictionary<string, ForeignShareholdingDetail>();
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

    private async Task<Dictionary<string, ForeignShareholdingDetail>> GetListedAsync(DateOnly date)
    {
        var dateStr = date.ToString("yyyyMMdd");
        var url = $"{TwseUrl}?date={dateStr}&selectType=ALLBUT0999&response=json";

        using var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var stat = root.TryGetProperty("stat", out var statEl) ? statEl.GetString() : null;
        if (stat != "OK" || !root.TryGetProperty("data", out var dataEl) || !root.TryGetProperty("fields", out var fieldsEl))
        {
            // 非交易日(假日)或當天尚未公布資料
            return new Dictionary<string, ForeignShareholdingDetail>();
        }

        var fields = fieldsEl.EnumerateArray().Select(f => f.GetString() ?? "").ToArray();
        return ParseRows(dataEl, fields, Market.Listed,
            codeField: "證券代號", nameField: "證券名稱", issuedField: "發行股數",
            availableSharesField: "外資及陸資尚可投資股數", heldSharesField: "全體外資及陸資持有股數",
            availableRatioField: "外資及陸資尚可投資比率", heldRatioField: "全體外資及陸資持股比率",
            source: "TWSE MI_QFIIS");
    }

    private async Task<Dictionary<string, ForeignShareholdingDetail>> GetOtcAsync(DateOnly date)
    {
        var dateParam = $"{date.Year:D4}/{date.Month:D2}/{date.Day:D2}";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["date"] = dateParam,
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

        var stat = root.TryGetProperty("stat", out var statEl) ? statEl.GetString() : null;
        if (stat != "ok" || !root.TryGetProperty("tables", out var tablesEl) || tablesEl.GetArrayLength() == 0)
        {
            return new Dictionary<string, ForeignShareholdingDetail>();
        }

        var table = tablesEl[0];
        if (!table.TryGetProperty("data", out var dataEl) || dataEl.GetArrayLength() == 0
            || !table.TryGetProperty("fields", out var fieldsEl))
        {
            // 非交易日(假日)或當天尚未公布資料，官方會回傳 stat=ok 但 data 是空陣列。
            return new Dictionary<string, ForeignShareholdingDetail>();
        }

        var fields = fieldsEl.EnumerateArray().Select(f => f.GetString() ?? "").ToArray();
        return ParseRows(dataEl, fields, Market.Otc,
            codeField: "代號", nameField: "名稱", issuedField: "發行股數(A)",
            availableSharesField: "僑外資及陸資尚可投資股數B=A*F-C", heldSharesField: "僑外資及陸資持有股數(C)",
            availableRatioField: "僑外資及陸資尚可投資比率(D=B/A)", heldRatioField: "僑外資及陸資持股比率(E=C/A)",
            source: "TPEx insti/qfii");
    }

    private static Dictionary<string, ForeignShareholdingDetail> ParseRows(
        JsonElement dataEl, string[] fields, Market market,
        string codeField, string nameField, string issuedField,
        string availableSharesField, string heldSharesField,
        string availableRatioField, string heldRatioField,
        string source)
    {
        int Idx(string name)
        {
            var i = Array.IndexOf(fields, name);
            if (i < 0)
            {
                throw new InvalidOperationException($"{source} 回應欄位格式異常，找不到「{name}」欄位。");
            }
            return i;
        }

        var codeIdx = Idx(codeField);
        var nameIdx = Idx(nameField);
        var issuedIdx = Idx(issuedField);
        var availableSharesIdx = Idx(availableSharesField);
        var heldSharesIdx = Idx(heldSharesField);
        var availableRatioIdx = Idx(availableRatioField);
        var heldRatioIdx = Idx(heldRatioField);

        // TWSE 的比率欄位是 JSON 數字，其餘欄位(含千分位逗號)是字串；
        // TPEx 全部都是字串，且比率欄位還帶 % 字尾，一律轉字串後統一解析，避免型別不一致炸掉。
        static string CellToString(JsonElement c) => c.ValueKind switch
        {
            JsonValueKind.String => c.GetString() ?? "",
            JsonValueKind.Null => "",
            _ => c.GetRawText(),
        };

        long NumLong(string[] cells, int idx) =>
            long.TryParse(cells[idx].Trim().Replace(",", ""), out var v) ? v : 0;

        double NumDouble(string[] cells, int idx) =>
            double.TryParse(cells[idx].Trim().Replace(",", "").Replace("%", ""), out var v) ? v : 0;

        var result = new Dictionary<string, ForeignShareholdingDetail>();
        foreach (var row in dataEl.EnumerateArray())
        {
            var cells = row.EnumerateArray().Select(CellToString).ToArray();
            if (cells.Length < fields.Length)
            {
                // 偶爾會有欄位數量不足的異常列，跳過避免索引超出範圍。
                continue;
            }

            var code = cells[codeIdx].Trim();
            if (string.IsNullOrEmpty(code))
            {
                continue;
            }

            result[code] = new ForeignShareholdingDetail
            {
                StockCode = code,
                StockName = cells[nameIdx].Trim(),
                Market = market,

                IssuedShares = NumLong(cells, issuedIdx),
                AvailableShares = NumLong(cells, availableSharesIdx),
                HeldShares = NumLong(cells, heldSharesIdx),
                AvailableRatio = NumDouble(cells, availableRatioIdx),
                HeldRatio = NumDouble(cells, heldRatioIdx),
            };
        }

        return result;
    }
}
