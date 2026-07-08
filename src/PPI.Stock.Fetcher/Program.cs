using Microsoft.Extensions.Configuration;
using PPI.Stock.Fetcher;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Development.json", optional: true)
    .Build();

var settings = config.GetSection("GoogleSheets").Get<GoogleSheetsSettings>()
    ?? throw new InvalidOperationException("找不到 GoogleSheets 設定區段，請檢查 appsettings.json。");

// 支援用命令列參數指定要補抓的日期，格式 yyyyMMdd；不帶參數則自動處理「今天、昨天」兩天。
// 排程一天執行兩次(17:30、18:30)，每次都重新抓這兩天的資料並跟既有資料比對更新：
// 一來讓第二次執行能補上第一次沒抓到的股票、二來讓「昨天」的資料有機會修正 API 偶發的錯誤數字。
// 注意：上櫃(TPEx)只能抓最新一天的資料，指定過去日期時上櫃部分會被跳過。
// 第二個參數可選：指定只補特定股票代號，用於某支股票剛加入 Watchlist、只想回補這一支的歷史資料，
// 避免重跑整個清單導致其他已經有資料的股票被重複寫入。
var explicitDate = args.Length > 0 ? DateOnly.ParseExact(args[0], "yyyyMMdd") : (DateOnly?)null;
var onlyStockCode = args.Length > 1 ? args[1] : null;

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

var datesToProcess = explicitDate != null
    ? new[] { explicitDate.Value }
    : new[] { DateOnly.FromDateTime(DateTime.Today), DateOnly.FromDateTime(DateTime.Today).AddDays(-1) };

foreach (var targetDate in datesToProcess)
{
    await ProcessDateAsync(targetDate, watchlist, sheets, twse, tpex);
}

static async Task ProcessDateAsync(
    DateOnly targetDate,
    List<string> watchlist,
    GoogleSheetsClient sheets,
    TwseClient twse,
    TpexClient tpex)
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 開始處理 {targetDate:yyyy-MM-dd} 的三大法人買賣超資料...");

    var listedTrades = await twse.GetInstitutionalTradesAsync(targetDate);
    var otcTrades = await tpex.GetInstitutionalTradesAsync(targetDate);

    if (listedTrades.Count == 0 && otcTrades.Count == 0)
    {
        Console.WriteLine($"{targetDate:yyyy-MM-dd} 上市、上櫃都沒有資料，可能是假日或非交易日，跳過。");
        return;
    }

    var rows = new List<(string Date, string Code, IList<object> Row)>();
    foreach (var code in watchlist)
    {
        if (listedTrades.TryGetValue(code, out var listedDetail))
        {
            rows.Add((targetDate.ToString("yyyy-MM-dd"), code, listedDetail.ToSheetRow(targetDate)));
        }
        else if (otcTrades.TryGetValue(code, out var otcDetail))
        {
            rows.Add((targetDate.ToString("yyyy-MM-dd"), code, otcDetail.ToSheetRow(targetDate)));
        }
        else
        {
            Console.WriteLine($"警告：{targetDate:yyyy-MM-dd} 上市、上櫃都找不到股票代號 {code} 的資料，請確認代號是否正確，或該股票當天無交易。");
        }
    }

    if (rows.Count == 0)
    {
        Console.WriteLine($"{targetDate:yyyy-MM-dd} 沒有任何一檔股票有資料可寫入。");
        return;
    }

    var (added, updated, unchanged) = await sheets.UpsertRowsAsync(rows);
    Console.WriteLine($"{targetDate:yyyy-MM-dd}：新增 {added} 筆、更新 {updated} 筆、未變 {unchanged} 筆。");
}
