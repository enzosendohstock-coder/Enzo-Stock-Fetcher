using Microsoft.Extensions.Configuration;
using PPI.Stock.Fetcher;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Development.json", optional: true)
    .Build();

var settings = config.GetSection("GoogleSheets").Get<GoogleSheetsSettings>()
    ?? throw new InvalidOperationException("找不到 GoogleSheets 設定區段，請檢查 appsettings.json。");

// 暫時的診斷模式：掃描整份資料分頁，列出所有 (日期, 代號) 重複的列，用來確認今天測試造成的資料重複範圍。
if (args.Length > 0 && args[0] == "--find-duplicates")
{
    await FindDuplicatesAsync(new GoogleSheetsClient(settings), settings);
    return;
}

// 支援用命令列參數指定要補抓的日期：
//   不帶參數                   → 自動處理「今天、往前 RollingWindowDays 天」(排程用)
//   單一日期 yyyyMMdd          → 只處理這一天
//   日期區間 yyyyMMdd-yyyyMMdd → 逐日處理整個區間，例如新股票剛加入 Watchlist，要一次回補過去的歷史資料
// 排程一天執行兩次(17:30、18:30)，每次都重新抓最近幾天的資料並跟既有資料比對更新：
// 一來讓第二次執行能補上第一次沒抓到的股票、二來讓最近幾天的資料有機會修正 API 偶發的錯誤數字或補上
// 兩次都失敗的缺漏(例如剛好那天 TWSE 兩次都回傳異常格式，隔天排程還有機會補上)。
// 注意：上櫃(TPEx)只能抓最新一天的資料，指定過去日期(含區間回補)時上櫃部分會被跳過，只有上市(TWSE)股票能回補歷史資料。
// 第二個參數可選：指定只補特定股票代號，用於某支股票剛加入 Watchlist、只想回補這一支的歷史資料，
// 避免重跑整個清單導致其他已經有資料的股票被重複寫入。
var explicitDates = args.Length > 0 ? ParseDateArg(args[0]) : null;
var onlyStockCode = args.Length > 1 ? args[1] : null;

// 新股票自動回補歷史資料一律回補到這個固定日期，不隨時間變動。
var historyStartDate = new DateOnly(2026, 1, 1);

// 自動排程模式每次往前重跑的天數(含今天)，用來讓偶發的抓取失敗有更多機會被下次執行自動補上。
const int RollingWindowDays = 5;

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

var datesToProcess = explicitDates
    ?? Enumerable.Range(0, RollingWindowDays)
        .Select(offset => DateOnly.FromDateTime(DateTime.Today).AddDays(-offset))
        .ToArray();

foreach (var targetDate in datesToProcess)
{
    // TWSE 偶爾會回傳格式異常的回應(例如缺欄位)，單一天出錯不該讓整個回補批次中斷，
    // 印警告、跳過這一天、繼續處理下一天即可，之後可以針對這天單獨重跑。
    try
    {
        await ProcessDateAsync(targetDate, watchlist, sheets, twse, tpex);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"警告：{targetDate:yyyy-MM-dd} 處理失敗，跳過並繼續下一天。錯誤：{ex.Message}");
    }

    // 區間回補會連續呼叫 TWSE API 很多次，稍微間隔一下比較不會對它造成負擔。
    if (datesToProcess.Length > 1)
    {
        await Task.Delay(300);
    }
}

// 只有自動排程模式(沒有帶任何命令列參數)才會檢查有沒有新股票需要自動回補歷史資料，
// 手動指定日期/區間跑的時候維持原本行為，不觸發這個額外步驟。
if (explicitDates == null)
{
    await BackfillNewStocksAsync(watchlist, sheets, twse, tpex, historyStartDate);
}

static async Task FindDuplicatesAsync(GoogleSheetsClient sheets, GoogleSheetsSettings settings)
{
    var rows = await sheets.GetAllRowsWithSheetRowAsync();
    var groups = rows
        .GroupBy(r => (r.Date, r.Code))
        .Where(g => g.Count() > 1)
        .OrderBy(g => g.Key.Date)
        .ThenBy(g => g.Key.Code)
        .ToList();

    Console.WriteLine($"總共 {rows.Count} 列資料，發現 {groups.Count} 組 (日期,代號) 重複：");
    foreach (var g in groups)
    {
        Console.WriteLine($"- {g.Key.Date} {g.Key.Code}：共 {g.Count()} 列，Sheet 列號 = {string.Join(", ", g.Select(r => r.SheetRow))}");
        foreach (var r in g)
        {
            Console.WriteLine($"    第 {r.SheetRow} 列：{string.Join(" | ", r.Values)}");
        }
    }
}

static async Task BackfillNewStocksAsync(
    List<string> watchlist,
    GoogleSheetsClient sheets,
    TwseClient twse,
    TpexClient tpex,
    DateOnly historyStartDate)
{
    var earliestByCode = await sheets.GetEarliestDateByCodeAsync();

    // 緩衝 14 天，避免把「本來就從 1 月中才有資料的正常股票」誤判成新股票
    // (年初這幾天常常剛好遇到假日，資料本來就不會剛好從歷史起始日當天開始)。
    var threshold = historyStartDate.AddDays(14);
    var newStocks = watchlist
        .Where(code => !earliestByCode.TryGetValue(code, out var earliest) || earliest > threshold)
        .ToList();

    if (newStocks.Count == 0)
    {
        return;
    }

    Console.WriteLine($"偵測到 {newStocks.Count} 檔股票沒有從 {historyStartDate:yyyy-MM-dd} 開始的歷史資料，自動回補中：{string.Join(", ", newStocks)}");

    var yesterday = DateOnly.FromDateTime(DateTime.Today).AddDays(-1);
    var backfillDates = new List<DateOnly>();
    for (var d = historyStartDate; d <= yesterday; d = d.AddDays(1))
    {
        backfillDates.Add(d);
    }

    foreach (var code in newStocks)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 開始自動回補股票代號 {code} 的歷史資料({historyStartDate:yyyy-MM-dd} ~ {yesterday:yyyy-MM-dd})...");

        var singleStockWatchlist = new List<string> { code };
        foreach (var targetDate in backfillDates)
        {
            try
            {
                await ProcessDateAsync(targetDate, singleStockWatchlist, sheets, twse, tpex);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"警告：{targetDate:yyyy-MM-dd} 處理失敗，跳過並繼續下一天。錯誤：{ex.Message}");
            }

            await Task.Delay(300);
        }

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 股票代號 {code} 的歷史回補完成。");
    }
}

static DateOnly[] ParseDateArg(string arg)
{
    var parts = arg.Split('-');
    if (parts.Length == 2)
    {
        var start = DateOnly.ParseExact(parts[0], "yyyyMMdd");
        var end = DateOnly.ParseExact(parts[1], "yyyyMMdd");
        if (end < start)
        {
            throw new ArgumentException($"日期區間結束日期 {end:yyyy-MM-dd} 不能早於開始日期 {start:yyyy-MM-dd}。");
        }

        var days = new List<DateOnly>();
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            days.Add(d);
        }
        return days.ToArray();
    }

    return new[] { DateOnly.ParseExact(arg, "yyyyMMdd") };
}

static async Task ProcessDateAsync(
    DateOnly targetDate,
    List<string> watchlist,
    GoogleSheetsClient sheets,
    TwseClient twse,
    TpexClient tpex)
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 開始處理 {targetDate:yyyy-MM-dd} 的三大法人買賣超資料...");

    Dictionary<string, InstitutionalTradeDetail> listedTrades;
    try
    {
        listedTrades = await twse.GetInstitutionalTradesAsync(targetDate);
    }
    catch (Exception ex)
    {
        // TWSE 偶爾會回傳格式異常的暫時性錯誤回應，等一下重試一次，通常就會拿到正常資料。
        Console.WriteLine($"警告：{targetDate:yyyy-MM-dd} TWSE 回應異常({ex.Message})，等待後重試一次...");
        await Task.Delay(2000);
        listedTrades = await twse.GetInstitutionalTradesAsync(targetDate);
    }

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
