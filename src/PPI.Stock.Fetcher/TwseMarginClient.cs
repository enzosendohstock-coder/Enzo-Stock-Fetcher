using System.Text.Json;

namespace PPI.Stock.Fetcher;

/// <summary>
/// 呼叫 TWSE 公開的融資融券餘額(MI_MARGN)與借券賣出餘額(TWT93U)報表，合併成每支股票
/// 一筆完整的 MarginTradingDetail，僅涵蓋上市股票。
/// 這兩份報表的欄位名稱都有重複(融資/融券兩組都叫「買進」「賣出」等)，不能像 TwseClient
/// 那樣用欄位名稱查找，改用固定欄位位置讀取，讀取前會先檢查欄位數量/開頭欄位是否符合預期，
/// 避免 TWSE 改版時默默讀到錯誤的欄位。
/// </summary>
public class TwseMarginClient
{
    private readonly HttpClient _httpClient;

    public TwseMarginClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<Dictionary<string, MarginTradingDetail>> GetMarginTradingAsync(DateOnly date)
    {
        var dateStr = date.ToString("yyyyMMdd");

        var marginTask = _httpClient.GetAsync($"https://www.twse.com.tw/rwd/zh/marginTrading/MI_MARGN?date={dateStr}&selectType=ALL&response=json");
        var sblTask = _httpClient.GetAsync($"https://www.twse.com.tw/rwd/zh/marginTrading/TWT93U?date={dateStr}&selectType=ALL&response=json");
        await Task.WhenAll(marginTask, sblTask);

        var marginRows = await ParseMarginTableAsync(await marginTask);
        var sblRows = await ParseSblTableAsync(await sblTask);

        var result = new Dictionary<string, MarginTradingDetail>();
        foreach (var (code, m) in marginRows)
        {
            sblRows.TryGetValue(code, out var sbl);

            result[code] = new MarginTradingDetail
            {
                StockCode = code,
                StockName = m.Name,
                Market = Market.Listed,

                MarginBuy = m.MarginBuy,
                MarginSell = m.MarginSell,
                MarginCashRedemption = m.MarginCashRedemption,
                MarginBalancePrev = m.MarginBalancePrev,
                MarginBalance = m.MarginBalance,
                MarginQuota = m.MarginQuota,

                ShortSell = m.ShortSell,
                ShortBuy = m.ShortBuy,
                ShortStockRedemption = m.ShortStockRedemption,
                ShortBalancePrev = m.ShortBalancePrev,
                ShortBalance = m.ShortBalance,
                ShortQuota = m.ShortQuota,

                Offsetting = m.Offsetting,

                SblBalancePrev = sbl?.SblBalancePrev ?? 0,
                SblSell = sbl?.SblSell ?? 0,
                SblReturn = sbl?.SblReturn ?? 0,
                SblAdjustment = sbl?.SblAdjustment ?? 0,
                SblBalance = sbl?.SblBalance ?? 0,
                SblQuota = sbl?.SblQuota ?? 0,
            };
        }

        return result;
    }

    private static async Task<Dictionary<string, MarginRow>> ParseMarginTableAsync(HttpResponseMessage response)
    {
        var result = new Dictionary<string, MarginRow>();
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var stat = root.TryGetProperty("stat", out var statEl) ? statEl.GetString() : null;
        if (stat != "OK" || !root.TryGetProperty("tables", out var tablesEl))
        {
            // 非交易日或當天尚未公布資料
            return result;
        }

        // MI_MARGN 回傳多個表格，要找「代號」開頭、16 欄的那個逐股明細表，不能假設固定索引位置。
        JsonElement? detailTable = null;
        foreach (var table in tablesEl.EnumerateArray())
        {
            if (!table.TryGetProperty("fields", out var fieldsEl) || fieldsEl.GetArrayLength() != 16)
            {
                continue;
            }
            var firstField = fieldsEl[0].GetString();
            if (firstField == "代號")
            {
                detailTable = table;
                break;
            }
        }

        if (detailTable == null || !detailTable.Value.TryGetProperty("data", out var dataEl))
        {
            return result;
        }

        foreach (var row in dataEl.EnumerateArray())
        {
            var cells = row.EnumerateArray().Select(CellToString).ToArray();
            if (cells.Length < 16)
            {
                continue;
            }

            var code = cells[0].Trim();
            if (string.IsNullOrEmpty(code))
            {
                continue;
            }

            // MI_MARGN 回傳的融資融券數字本身是「張」，借券賣出(TWT93U)那份是「股」，
            // 兩份單位不一致。統一都換算成股存進 Google Sheet，畫面呈現時再一律除以 1000，
            // 這樣看原始資料不用另外記哪些欄位是張、哪些是股。
            result[code] = new MarginRow
            {
                Name = cells[1].Trim(),
                MarginBuy = NumLots(cells[2]),
                MarginSell = NumLots(cells[3]),
                MarginCashRedemption = NumLots(cells[4]),
                MarginBalancePrev = NumLots(cells[5]),
                MarginBalance = NumLots(cells[6]),
                MarginQuota = NumLots(cells[7]),
                ShortSell = NumLots(cells[9]),
                ShortBuy = NumLots(cells[8]),
                ShortStockRedemption = NumLots(cells[10]),
                ShortBalancePrev = NumLots(cells[11]),
                ShortBalance = NumLots(cells[12]),
                ShortQuota = NumLots(cells[13]),
                Offsetting = NumLots(cells[14]),
            };
        }

        return result;
    }

    private static async Task<Dictionary<string, SblRow>> ParseSblTableAsync(HttpResponseMessage response)
    {
        var result = new Dictionary<string, SblRow>();
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var stat = root.TryGetProperty("stat", out var statEl) ? statEl.GetString() : null;
        if (stat != "OK" || !root.TryGetProperty("fields", out var fieldsEl) || fieldsEl.GetArrayLength() != 15
            || !root.TryGetProperty("data", out var dataEl))
        {
            return result;
        }

        foreach (var row in dataEl.EnumerateArray())
        {
            var cells = row.EnumerateArray().Select(CellToString).ToArray();
            if (cells.Length < 14)
            {
                continue;
            }

            var code = cells[0].Trim();
            if (string.IsNullOrEmpty(code))
            {
                continue;
            }

            // idx 2-7 是融券(跟 MI_MARGN 重複，不取)，idx 8-13 是借券賣出，才是我們要的。
            result[code] = new SblRow
            {
                SblBalancePrev = Num(cells[8]),
                SblSell = Num(cells[9]),
                SblReturn = Num(cells[10]),
                SblAdjustment = Num(cells[11]),
                SblBalance = Num(cells[12]),
                SblQuota = Num(cells[13]),
            };
        }

        return result;
    }

    private static long Num(string cell) =>
        long.TryParse(cell.Trim().Replace(",", ""), out var v) ? v : 0;

    // MI_MARGN 的數字單位是「張」，乘以 1000 換算成股，跟借券賣出(已經是股)的欄位單位一致。
    private static long NumLots(string cell) => Num(cell) * 1000;

    // TWSE 偶爾會把儲存格回傳成 JSON 數字而非字串(型別不一致)，一律用原始文字讀出避免解析炸掉。
    private static string CellToString(JsonElement c) => c.ValueKind switch
    {
        JsonValueKind.String => c.GetString() ?? "",
        JsonValueKind.Null => "",
        _ => c.GetRawText(),
    };

    private class MarginRow
    {
        public string Name { get; init; } = "";
        public long MarginBuy { get; init; }
        public long MarginSell { get; init; }
        public long MarginCashRedemption { get; init; }
        public long MarginBalancePrev { get; init; }
        public long MarginBalance { get; init; }
        public long MarginQuota { get; init; }
        public long ShortSell { get; init; }
        public long ShortBuy { get; init; }
        public long ShortStockRedemption { get; init; }
        public long ShortBalancePrev { get; init; }
        public long ShortBalance { get; init; }
        public long ShortQuota { get; init; }
        public long Offsetting { get; init; }
    }

    private class SblRow
    {
        public long SblBalancePrev { get; init; }
        public long SblSell { get; init; }
        public long SblReturn { get; init; }
        public long SblAdjustment { get; init; }
        public long SblBalance { get; init; }
        public long SblQuota { get; init; }
    }
}
