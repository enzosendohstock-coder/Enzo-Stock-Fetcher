namespace PPI.Stock.Fetcher;

public class WorkerApiSettings
{
    public string BaseUrl { get; set; } = "";

    // 刻意不放在 appsettings.json 裡（那個檔案會被上傳到 GitHub），改由環境變數
    // WorkerApi__ApiToken 提供，本機執行時用 $env:WorkerApi__ApiToken 設定，
    // GitHub Actions 執行時從 secrets.FETCHER_API_TOKEN 注入。
    public string ApiToken { get; set; } = "";
}
