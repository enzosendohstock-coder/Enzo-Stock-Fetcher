using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;

namespace PPI.Stock.Fetcher;

/// <summary>
/// 讀取 Watchlist 試算表的股票代號，並將抓到的資料寫入 InstitutionalTrades 試算表。
/// 兩者是各自獨立的試算表(不同 SpreadsheetId)，共用同一組服務帳號憑證存取。
/// </summary>
public class GoogleSheetsClient
{
    private readonly GoogleSheetsSettings _settings;
    private readonly SheetsService _service;

    public GoogleSheetsClient(GoogleSheetsSettings settings)
    {
        _settings = settings;

        var credential = CredentialFactory
            .FromFile<ServiceAccountCredential>(settings.CredentialsFilePath)
            .ToGoogleCredential()
            .CreateScoped(SheetsService.Scope.Spreadsheets);

        _service = new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "PPI.Stock.Fetcher",
        });
    }

    /// <summary>
    /// 讀取追蹤清單分頁的股票代號(去除空白列與重複)。
    /// </summary>
    public async Task<List<string>> GetWatchlistAsync()
    {
        var range = $"{QuoteSheetName(_settings.WatchlistSheetName)}!{_settings.WatchlistRange}";
        var request = _service.Spreadsheets.Values.Get(_settings.WatchlistSpreadsheetId, range);
        var response = await request.ExecuteAsync();

        var codes = new List<string>();
        if (response.Values == null)
        {
            return codes;
        }

        foreach (var row in response.Values)
        {
            if (row.Count == 0)
            {
                continue;
            }

            var code = row[0]?.ToString()?.Trim();
            if (!string.IsNullOrEmpty(code) && !codes.Contains(code))
            {
                codes.Add(code);
            }
        }

        return codes;
    }

    /// <summary>
    /// 將資料列附加到資料分頁的最後面。
    /// </summary>
    public async Task AppendRowsAsync(IEnumerable<IList<object>> rows)
    {
        var range = $"{QuoteSheetName(_settings.DataSheetName)}!{_settings.DataRange}";
        var body = new ValueRange { Values = rows.ToList() };

        var request = _service.Spreadsheets.Values.Append(body, _settings.DataSpreadsheetId, range);
        request.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;
        request.InsertDataOption = SpreadsheetsResource.ValuesResource.AppendRequest.InsertDataOptionEnum.INSERTROWS;

        await request.ExecuteAsync();
    }

    /// <summary>
    /// 統計每個股票代號目前在資料分頁裡最早的日期，用來判斷哪些股票還沒有回補過歷史資料
    /// (例如剛加入 Watchlist 的新股票)。沒有資料的代號不會出現在回傳的字典裡。
    /// </summary>
    public async Task<Dictionary<string, DateOnly>> GetEarliestDateByCodeAsync()
    {
        var range = $"{QuoteSheetName(_settings.DataSheetName)}!{_settings.DataRange}";
        var getRequest = _service.Spreadsheets.Values.Get(_settings.DataSpreadsheetId, range);
        getRequest.ValueRenderOption = SpreadsheetsResource.ValuesResource.GetRequest.ValueRenderOptionEnum.UNFORMATTEDVALUE;
        var existing = await getRequest.ExecuteAsync();

        var earliestByCode = new Dictionary<string, DateOnly>();
        if (existing.Values == null)
        {
            return earliestByCode;
        }

        for (var i = 1; i < existing.Values.Count; i++)
        {
            var row = existing.Values[i];
            if (row.Count < 3)
            {
                continue;
            }

            var dateStr = NormalizeDate(row[0]);
            var code = row[2]?.ToString()?.Trim() ?? "";
            if (dateStr == null || string.IsNullOrEmpty(code) || !DateOnly.TryParse(dateStr, out var date))
            {
                continue;
            }

            if (!earliestByCode.TryGetValue(code, out var existingEarliest) || date < existingEarliest)
            {
                earliestByCode[code] = date;
            }
        }

        return earliestByCode;
    }

    /// <summary>
    /// 讀出資料分頁裡每一列的原始內容跟它在 Sheet 上實際的列號(1-based)，
    /// 用於診斷/清理重複資料等維護用途。
    /// </summary>
    public async Task<List<(int SheetRow, string Date, string Code, IList<object> Values)>> GetAllRowsWithSheetRowAsync()
    {
        var range = $"{QuoteSheetName(_settings.DataSheetName)}!{_settings.DataRange}";
        var getRequest = _service.Spreadsheets.Values.Get(_settings.DataSpreadsheetId, range);
        getRequest.ValueRenderOption = SpreadsheetsResource.ValuesResource.GetRequest.ValueRenderOptionEnum.UNFORMATTEDVALUE;
        var existing = await getRequest.ExecuteAsync();

        var result = new List<(int SheetRow, string Date, string Code, IList<object> Values)>();
        if (existing.Values == null)
        {
            return result;
        }

        for (var i = 1; i < existing.Values.Count; i++)
        {
            var row = existing.Values[i];
            if (row.Count < 3)
            {
                continue;
            }

            var dateStr = NormalizeDate(row[0]);
            var code = row[2]?.ToString()?.Trim() ?? "";
            if (dateStr == null || string.IsNullOrEmpty(code))
            {
                continue;
            }

            result.Add((i + 1, dateStr, code, row));
        }

        return result;
    }

    /// <summary>
    /// 依 (日期, 股票代號) 比對資料分頁裡已存在的資料：
    /// 不存在就新增、存在但內容不同就覆蓋更新、內容相同就跳過不動作。
    /// 用來支援一天抓兩次、以及回頭比對前一天資料的排程需求，避免重複寫入或誤判有變化。
    /// </summary>
    public async Task<(int Added, int Updated, int Unchanged)> UpsertRowsAsync(
        IEnumerable<(string Date, string Code, IList<object> Row)> rows)
    {
        var range = $"{QuoteSheetName(_settings.DataSheetName)}!{_settings.DataRange}";
        var getRequest = _service.Spreadsheets.Values.Get(_settings.DataSpreadsheetId, range);
        getRequest.ValueRenderOption = SpreadsheetsResource.ValuesResource.GetRequest.ValueRenderOptionEnum.UNFORMATTEDVALUE;
        var existing = await getRequest.ExecuteAsync();

        var rowIndexByKey = new Dictionary<(string Date, string Code), int>();
        var existingRowsByKey = new Dictionary<(string Date, string Code), IList<object>>();

        if (existing.Values != null)
        {
            // index 0 是表頭列，跳過；i 是 0-based 索引，對應到 Sheet 的第 (i + 1) 列(1-based)。
            for (var i = 1; i < existing.Values.Count; i++)
            {
                var row = existing.Values[i];
                if (row.Count < 3)
                {
                    continue;
                }

                var dateKey = NormalizeDate(row[0]);
                var code = row[2]?.ToString()?.Trim() ?? "";
                if (dateKey == null || string.IsNullOrEmpty(code))
                {
                    continue;
                }

                var key = (dateKey, code);
                if (rowIndexByKey.TryGetValue(key, out var previousRow))
                {
                    // 正常情況下每個 (日期,代號) 只會有一列，如果偵測到超過一列，
                    // 代表資料分頁裡已經有重複資料(例如過去程式 bug 或人工誤操作造成)。
                    // 這裡不自動刪除，只印警告，避免自動化程式在使用者沒注意到的情況下擅自刪資料。
                    Console.WriteLine($"警告：偵測到 {dateKey} {code} 有重複資料(第 {previousRow} 列與第 {i + 1} 列)，建議手動檢查並清理，目前先以最後一筆為準繼續處理。");
                }

                rowIndexByKey[key] = i + 1;
                existingRowsByKey[key] = row;
            }
        }

        var toAppend = new List<IList<object>>();
        var updateData = new List<ValueRange>();
        var added = 0;
        var updated = 0;
        var unchanged = 0;

        foreach (var (date, code, row) in rows)
        {
            var key = (date, code);
            if (rowIndexByKey.TryGetValue(key, out var sheetRow))
            {
                if (RowsEqual(existingRowsByKey[key], row))
                {
                    unchanged++;
                    continue;
                }

                var updateRange = $"{QuoteSheetName(_settings.DataSheetName)}!A{sheetRow}:Z{sheetRow}";
                updateData.Add(new ValueRange { Range = updateRange, Values = new List<IList<object>> { row } });
                updated++;
            }
            else
            {
                toAppend.Add(row);
                added++;
            }
        }

        if (updateData.Count > 0)
        {
            var batchBody = new BatchUpdateValuesRequest
            {
                ValueInputOption = "USER_ENTERED",
                Data = updateData,
            };
            await _service.Spreadsheets.Values.BatchUpdate(batchBody, _settings.DataSpreadsheetId).ExecuteAsync();
        }

        if (toAppend.Count > 0)
        {
            await AppendRowsAsync(toAppend);
        }

        return (added, updated, unchanged);
    }

    /// <summary>
    /// 讀回來的日期儲存格可能是純文字("2026-07-08")，也可能被 Google Sheets 自動轉成
    /// 內部的日期序號(UNFORMATTED_VALUE 模式下實測會是 Int64，理論上也可能是 double)，
    /// 這裡都要能正確轉成統一格式比對，不然日期比對永遠對不上，upsert 就會一直誤判成新資料。
    /// </summary>
    private static string? NormalizeDate(object cell) => cell switch
    {
        string s when DateOnly.TryParse(s, out var d) => d.ToString("yyyy-MM-dd"),
        double d => DateOnly.FromDateTime(DateTime.FromOADate(d)).ToString("yyyy-MM-dd"),
        long l => DateOnly.FromDateTime(DateTime.FromOADate(l)).ToString("yyyy-MM-dd"),
        int i => DateOnly.FromDateTime(DateTime.FromOADate(i)).ToString("yyyy-MM-dd"),
        _ => null,
    };

    /// <summary>
    /// 把儲存格值統一轉成字串比較，數字一律轉成整數字串，避免 123 跟 123.0 被誤判成不同。
    /// </summary>
    private static string Canon(object? cell) => cell switch
    {
        null => "",
        string s => s.Trim(),
        double d => ((long)Math.Round(d)).ToString(),
        _ => cell.ToString() ?? "",
    };

    /// <summary>
    /// 第 0 欄(日期)用 NormalizeDate 比對，因為既有列可能被 Sheets 存成日期序號、
    /// 新資料是純文字，兩種型態不同但代表同一天時 Canon 直接比字串會誤判成不同；
    /// 其餘欄位維持用 Canon 做一般數字/文字比較。
    /// </summary>
    private static bool RowsEqual(IList<object> existingRow, IList<object> newRow)
    {
        var len = Math.Max(existingRow.Count, newRow.Count);
        for (var i = 0; i < len; i++)
        {
            var existingCell = i < existingRow.Count ? existingRow[i] : null;
            var newCell = i < newRow.Count ? newRow[i] : null;

            var a = i == 0 && existingCell != null ? NormalizeDate(existingCell) ?? Canon(existingCell) : Canon(existingCell);
            var b = i == 0 && newCell != null ? NormalizeDate(newCell) ?? Canon(newCell) : Canon(newCell);

            if (a != b)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A1 表示法中，分頁名稱含空白或特殊字元時必須用單引號包住，
    /// 這裡一律加上單引號並跳脫既有的單引號，不管分頁名稱是中文、英文或帶空白都能正確組出 range。
    /// </summary>
    private static string QuoteSheetName(string sheetName) => $"'{sheetName.Replace("'", "''")}'";
}
