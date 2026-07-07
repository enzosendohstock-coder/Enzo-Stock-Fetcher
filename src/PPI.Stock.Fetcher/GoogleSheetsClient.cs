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
    /// A1 表示法中，分頁名稱含空白或特殊字元時必須用單引號包住，
    /// 這裡一律加上單引號並跳脫既有的單引號，不管分頁名稱是中文、英文或帶空白都能正確組出 range。
    /// </summary>
    private static string QuoteSheetName(string sheetName) => $"'{sheetName.Replace("'", "''")}'";
}
