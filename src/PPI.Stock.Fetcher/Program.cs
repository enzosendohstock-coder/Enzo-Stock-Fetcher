using Microsoft.Extensions.Configuration;
using PPI.Stock.Fetcher;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Development.json", optional: true)
    .Build();

var settings = config.GetSection("GoogleSheets").Get<GoogleSheetsSettings>()
    ?? throw new InvalidOperationException("找不到 GoogleSheets 設定區段，請檢查 appsettings.json。");

// 支援用命令列參數指定要補抓的日期，格式 yyyyMMdd；不帶參數則抓今天。
// 注意：上櫃(TPEx)只能抓最新一天的資料，指定過去日期時上櫃部分會被跳過。
// 第二個參數可選：指定只補特定股票代號，用於某支股票剛加入 Watchlist、只想回補這一支的歷史資料，
// 避免重跑整個清單導致其他已經有資料的股票被重複寫入。
var targetDate = args.Length > 0
    ? DateOnly.ParseExact(args[0], "yyyyMMdd")
    : DateOnly.FromDateTime(DateTime.Today);
var onlyStockCode = args.Length > 1 ? args[1] : null;

Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 開始抓取 {targetDate:yyyy-MM-dd} 的三大法人買賣超資料...");

var sheets = new GoogleSheetsClient(settings);
var watchlist = await sheets.GetWatchlistAsync();

if (watchlist.Count == 0)
{
    Console.WriteLine("追蹤清單是空的，請先到 Google Sheet 的「追蹤清單」分頁新增股票代號。");
    return;
}

if (onlyStockCode != null)
{
    if (!watchlist.Contains(onlyStockCode))
    {
        Console.WriteLine($"警告：指定的股票代號 {onlyStockCode} 不在 Watchlist 裡，請確認代號是否正確。");
        return;
    }
    watchlist = new List<string> { onlyStockCode };
}

Console.WriteLine($"追蹤清單共 {watchlist.Count} 檔股票：{string.Join(", ", watchlist)}");

using var httpClient = new HttpClient();
var twse = new TwseClient(httpClient);
var tpex = new TpexClient(httpClient);

var listedTrades = await twse.GetInstitutionalTradesAsync(targetDate);
var otcTrades = await tpex.GetInstitutionalTradesAsync(targetDate);

if (listedTrades.Count == 0 && otcTrades.Count == 0)
{
    Console.WriteLine($"{targetDate:yyyy-MM-dd} 上市、上櫃都沒有資料，可能是假日或非交易日，跳過寫入。");
    return;
}

var rows = new List<IList<object>>();
foreach (var code in watchlist)
{
    if (listedTrades.TryGetValue(code, out var listedDetail))
    {
        rows.Add(listedDetail.ToSheetRow(targetDate));
    }
    else if (otcTrades.TryGetValue(code, out var otcDetail))
    {
        rows.Add(otcDetail.ToSheetRow(targetDate));
    }
    else
    {
        Console.WriteLine($"警告：上市、上櫃都找不到股票代號 {code} 的資料，請確認代號是否正確，或該股票當天無交易。");
    }
}

if (rows.Count == 0)
{
    Console.WriteLine("沒有任何一檔股票有資料可寫入。");
    return;
}

await sheets.AppendRowsAsync(rows);
Console.WriteLine($"完成，已寫入 {rows.Count} 筆資料到「{settings.DataSheetName}」分頁。");
