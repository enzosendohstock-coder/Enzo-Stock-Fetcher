namespace PPI.Stock.Fetcher;

public class GoogleSheetsSettings
{
    public string CredentialsFilePath { get; set; } = "";

    // Watchlist 是跨主題共用的股票主檔，獨立一份試算表，方便以後其他資料集(例如融資融券)共用同一份清單。
    public string WatchlistSpreadsheetId { get; set; } = "";
    public string WatchlistSheetName { get; set; } = "Watchlist";
    public string WatchlistRange { get; set; } = "A2:A";

    // InstitutionalTrades 是三大法人買賣超這個主題專屬的資料，另外獨立一份試算表。
    public string DataSpreadsheetId { get; set; } = "";
    public string DataSheetName { get; set; } = "InstitutionalTrades";
    public string DataRange { get; set; } = "A:Z";
}
