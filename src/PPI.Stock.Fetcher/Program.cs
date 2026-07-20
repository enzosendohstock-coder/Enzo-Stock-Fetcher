using Microsoft.Extensions.Configuration;
using PPI.Stock.Fetcher;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Development.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var settings = config.GetSection("GoogleSheets").Get<GoogleSheetsSettings>()
    ?? throw new InvalidOperationException("找不到 GoogleSheets 設定區段，請檢查 appsettings.json。");

// 暫時的診斷模式：掃描整份資料分頁，列出所有 (日期, 代號) 重複的列。
// 第二個參數可選：'margin' 檢查融資融券借券分頁，不給則檢查三大法人買賣超分頁。
if (args.Length > 0 && args[0] == "--find-duplicates")
{
    var checkMargin = args.Length > 1 && args[1] == "margin";
    var (spreadsheetId, sheetName, range) = checkMargin
        ? (settings.MarginDataSpreadsheetId, settings.MarginDataSheetName, settings.MarginDataRange)
        : (settings.DataSpreadsheetId, settings.DataSheetName, settings.DataRange);
    await FindDuplicatesAsync(new GoogleSheetsClient(settings), spreadsheetId, sheetName, range);
    return;
}

// 一次性搬遷模式：把 Google Sheets 現有的 Watchlist、三大法人買賣超、融資融券借券資料，
// 整批讀出來 POST 進 Cloudflare Worker 寫進 D1。因為打的是正式的 upsert 端點，天生具冪等性，
// 中途失敗可以直接重跑不會產生重複資料。驗證完成、確認資料無誤後這個模式跟本檔案就可以整段刪除。
if (args.Length > 0 && args[0] == "--migrate-to-d1")
{
    var workerSettings = config.GetSection("WorkerApi").Get<WorkerApiSettings>()
        ?? throw new InvalidOperationException("找不到 WorkerApi 設定區段，請檢查 appsettings.json。");
    if (string.IsNullOrEmpty(workerSettings.ApiToken))
    {
        Console.WriteLine("錯誤：找不到 WorkerApi ApiToken，請先設定環境變數 WorkerApi__ApiToken 再執行。");
        return;
    }

    using var migrateHttpClient = new HttpClient();
    var workerApi = new WorkerApiClient(migrateHttpClient, workerSettings);
    await MigrateToD1Async(new GoogleSheetsClient(settings), workerApi, settings);
    return;
}

// 支援用命令列參數指定要補抓的日期：
//   不帶參數                   → 自動處理「今天、往前 RollingWindowDays 天」(排程用)
//   單一日期 yyyyMMdd          → 只處理這一天
//   日期區間 yyyyMMdd-yyyyMMdd → 逐日處理整個區間，例如新股票剛加入 Watchlist，要一次回補過去的歷史資料
// 排程一天執行兩次(17:30、18:30)，每次都重新抓最近幾天的資料並跟既有資料比對更新：
// 一來讓第二次執行能補上第一次沒抓到的股票、二來讓最近幾天的資料有機會修正 API 偶發的錯誤數字或補上
// 兩次都失敗的缺漏(例如剛好那天 TWSE 兩次都回傳異常格式，隔天排程還有機會補上)。
// 上市(TWSE)、上櫃(TPEx，2026-07 起改用新端點)都支援指定任意歷史日期回補。
// 第二個參數可選：指定只補特定股票代號。
// 兩個參數可以分開用：只給股票代號、不給日期(第一個參數傳空字串)，代表「跟自動排程一樣的
// 滾動視窗 + 自我修復回補邏輯，但只處理這一支股票」，這是給前端「手動補資料」按鈕用的模式——
// 沿用同一套安全機制(307 封鎖偵測、只在真的缺資料時才回補)，不用另外寫一條路徑。
var explicitDates = args.Length > 0 && !string.IsNullOrEmpty(args[0]) ? ParseDateArg(args[0]) : null;
var onlyStockCode = args.Length > 1 && !string.IsNullOrEmpty(args[1]) ? args[1] : null;

// 新股票自動回補歷史資料一律回補到這個固定日期，不隨時間變動。
var historyStartDate = new DateOnly(2026, 1, 1);

// 自動排程模式每次往前重跑的天數(含今天)，用來讓偶發的抓取失敗有更多機會被下次執行自動補上。
const int RollingWindowDays = 5;

var workerApiSettings = config.GetSection("WorkerApi").Get<WorkerApiSettings>()
    ?? throw new InvalidOperationException("找不到 WorkerApi 設定區段，請檢查 appsettings.json。");
if (string.IsNullOrEmpty(workerApiSettings.ApiToken))
{
    Console.WriteLine("錯誤：找不到 WorkerApi ApiToken，請先設定環境變數 WorkerApi__ApiToken 再執行。");
    return;
}

// 用獨立的 HttpClient 呼叫 Worker，避免它預設帶的 Bearer Authorization header
// 被誤帶到下面 TWSE/TPEx 的請求裡。
using var workerHttpClient = new HttpClient();
var api = new WorkerApiClient(workerHttpClient, workerApiSettings);
var watchlist = await api.GetWatchlistAsync();

if (watchlist.Count == 0)
{
    Console.WriteLine("追蹤清單是空的，請先到「新增追蹤股票」頁面新增股票代號。");
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
var twseMargin = new TwseMarginClient(httpClient);
var tpexMargin = new TpexMarginClient(httpClient);
var foreignShareholding = new ForeignShareholdingClient(httpClient);

var datesToProcess = explicitDates
    ?? Enumerable.Range(0, RollingWindowDays)
        .Select(offset => DateOnly.FromDateTime(DateTime.Today).AddDays(-offset))
        .ToArray();

foreach (var targetDate in datesToProcess)
{
    // TWSE 偶爾會回傳格式異常的回應(例如缺欄位)，單一天出錯不該讓整個回補批次中斷，
    // 印警告、跳過這一天、繼續處理下一天即可，之後可以針對這天單獨重跑。
    // 但如果是被 TWSE 暫時封鎖(307 導向)，代表繼續送更多請求也沒用，整批直接中止。
    try
    {
        await ProcessInstitutionalDateAsync(targetDate, watchlist, api, twse, tpex);
    }
    catch (Exception ex) when (IsTwseBlocked(ex))
    {
        Console.WriteLine("警告：TWSE 疑似暫時封鎖本次執行的來源(收到 307 導向)，立即停止本次執行，留到下次排程接著跑。");
        return;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"警告：{targetDate:yyyy-MM-dd} 三大法人處理失敗，跳過並繼續下一天。錯誤：{ex.Message}");
    }

    try
    {
        await ProcessMarginDateAsync(targetDate, watchlist, api, twseMargin, tpexMargin);
    }
    catch (Exception ex) when (IsTwseBlocked(ex))
    {
        Console.WriteLine("警告：TWSE 疑似暫時封鎖本次執行的來源(收到 307 導向)，立即停止本次執行，留到下次排程接著跑。");
        return;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"警告：{targetDate:yyyy-MM-dd} 融資融券處理失敗，跳過並繼續下一天。錯誤：{ex.Message}");
    }

    try
    {
        await ProcessForeignShareholdingDateAsync(targetDate, watchlist, api, foreignShareholding);
    }
    catch (Exception ex) when (IsTwseBlocked(ex))
    {
        Console.WriteLine("警告：TWSE 疑似暫時封鎖本次執行的來源(收到 307 導向)，立即停止本次執行，留到下次排程接著跑。");
        return;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"警告：{targetDate:yyyy-MM-dd} 外資持股比率處理失敗，跳過並繼續下一天。錯誤：{ex.Message}");
    }

    // 區間回補會連續呼叫 API 很多次，稍微間隔一下比較不會對 TWSE/TPEx 造成負擔。
    if (datesToProcess.Length > 1)
    {
        await Task.Delay(300);
    }
}

// 只有自動排程模式(沒有帶任何命令列參數)才會檢查有沒有新股票需要自動回補歷史資料，
// 手動指定日期/區間跑的時候維持原本行為，不觸發這個額外步驟。
if (explicitDates == null)
{
    await BackfillNewStocksAsync(watchlist, api, twse, tpex, twseMargin, tpexMargin, foreignShareholding, historyStartDate);
}

static async Task FindDuplicatesAsync(GoogleSheetsClient sheets, string spreadsheetId, string sheetName, string dataRange)
{
    var rows = await sheets.GetAllRowsWithSheetRowAsync(spreadsheetId, sheetName, dataRange);
    var groups = rows
        .GroupBy(r => (r.Date, r.Code))
        .Where(g => g.Count() > 1)
        .OrderBy(g => g.Key.Date)
        .ThenBy(g => g.Key.Code)
        .ToList();

    Console.WriteLine($"「{sheetName}」分頁總共 {rows.Count} 列資料，發現 {groups.Count} 組 (日期,代號) 重複：");
    foreach (var g in groups)
    {
        Console.WriteLine($"- {g.Key.Date} {g.Key.Code}：共 {g.Count()} 列，Sheet 列號 = {string.Join(", ", g.Select(r => r.SheetRow))}");
        foreach (var r in g)
        {
            Console.WriteLine($"    第 {r.SheetRow} 列：{string.Join(" | ", r.Values)}");
        }
    }
}

static async Task MigrateToD1Async(GoogleSheetsClient sheets, WorkerApiClient api, GoogleSheetsSettings settings)
{
    Console.WriteLine("=== 搬遷 Watchlist ===");
    var watchlistDetails = await sheets.GetWatchlistDetailsAsync();
    var watchlistAdded = 0;
    foreach (var (code, shortName, fullName) in watchlistDetails)
    {
        if (await api.TryAddWatchlistEntryAsync(code, shortName, fullName))
        {
            watchlistAdded++;
        }
    }
    Console.WriteLine($"Watchlist 共 {watchlistDetails.Count} 筆，成功寫入/已存在 {watchlistAdded} 筆。");

    Console.WriteLine("=== 搬遷三大法人買賣超 ===");
    var institutionalRows = await sheets.GetAllRowsWithSheetRowAsync(settings.DataSpreadsheetId, settings.DataSheetName, settings.DataRange);
    await MigrateRowsByDateAsync(institutionalRows, MapInstitutionalRow, api.UpsertInstitutionalRawAsync, "三大法人");

    Console.WriteLine("=== 搬遷融資融券借券 ===");
    var marginRows = await sheets.GetAllRowsWithSheetRowAsync(settings.MarginDataSpreadsheetId, settings.MarginDataSheetName, settings.MarginDataRange);
    await MigrateRowsByDateAsync(marginRows, MapMarginRow, api.UpsertMarginRawAsync, "融資融券");

    Console.WriteLine("=== 搬遷完成 ===");
}

static async Task MigrateRowsByDateAsync(
    List<(int SheetRow, string Date, string Code, IList<object> Values)> sheetRows,
    Func<IList<object>, object> mapRow,
    Func<DateOnly, IEnumerable<object>, Task<(int Added, int Updated, int Unchanged)>> upsert,
    string label)
{
    var totalAdded = 0;
    var totalUpdated = 0;
    var totalUnchanged = 0;

    var byDate = sheetRows.GroupBy(r => r.Date).OrderBy(g => g.Key);
    foreach (var group in byDate)
    {
        if (!DateOnly.TryParse(group.Key, out var date))
        {
            Console.WriteLine($"警告：無法解析日期 {group.Key}，跳過這組 {group.Count()} 筆資料。");
            continue;
        }

        var rows = group.Select(r => mapRow(r.Values)).ToList();
        var (added, updated, unchanged) = await upsert(date, rows);
        totalAdded += added;
        totalUpdated += updated;
        totalUnchanged += unchanged;
        Console.WriteLine($"{label} {group.Key}：新增 {added}、更新 {updated}、未變 {unchanged}。");
    }

    Console.WriteLine($"{label} 搬遷總計：新增 {totalAdded}、更新 {totalUpdated}、未變 {totalUnchanged}。");
}

// 依 InstitutionalTradeDetail.ToSheetRow() 的欄位順序(26 欄)還原成 Worker 寫入端點要的 JSON 列。
static object MapInstitutionalRow(IList<object> row) => new
{
    market = ToStr(row, 1),
    stockCode = ToStr(row, 2),
    stockName = ToStr(row, 3),
    foreignExDealerBuy = ToLong(row, 4),
    foreignExDealerSell = ToLong(row, 5),
    foreignExDealerNet = ToLong(row, 6),
    foreignDealerBuy = ToLong(row, 7),
    foreignDealerSell = ToLong(row, 8),
    foreignDealerNet = ToLong(row, 9),
    foreignTotalBuy = ToLong(row, 10),
    foreignTotalSell = ToLong(row, 11),
    foreignTotalNet = ToLong(row, 12),
    trustBuy = ToLong(row, 13),
    trustSell = ToLong(row, 14),
    trustNet = ToLong(row, 15),
    dealerSelfBuy = ToNullableLong(row, 16),
    dealerSelfSell = ToNullableLong(row, 17),
    dealerSelfNet = ToNullableLong(row, 18),
    dealerHedgeBuy = ToNullableLong(row, 19),
    dealerHedgeSell = ToNullableLong(row, 20),
    dealerHedgeNet = ToNullableLong(row, 21),
    dealerTotalBuy = ToLong(row, 22),
    dealerTotalSell = ToLong(row, 23),
    dealerTotalNet = ToLong(row, 24),
    grandTotalNet = ToLong(row, 25),
};

// 依 MarginTradingDetail.ToSheetRow() 的欄位順序(23 欄)還原成 Worker 寫入端點要的 JSON 列。
static object MapMarginRow(IList<object> row) => new
{
    market = ToStr(row, 1),
    stockCode = ToStr(row, 2),
    stockName = ToStr(row, 3),
    marginBuy = ToLong(row, 4),
    marginSell = ToLong(row, 5),
    marginCashRedemption = ToLong(row, 6),
    marginBalancePrev = ToLong(row, 7),
    marginBalance = ToLong(row, 8),
    marginQuota = ToLong(row, 9),
    shortSell = ToLong(row, 10),
    shortBuy = ToLong(row, 11),
    shortStockRedemption = ToLong(row, 12),
    shortBalancePrev = ToLong(row, 13),
    shortBalance = ToLong(row, 14),
    shortQuota = ToLong(row, 15),
    offsetting = ToLong(row, 16),
    sblBalancePrev = ToLong(row, 17),
    sblSell = ToLong(row, 18),
    sblReturn = ToLong(row, 19),
    sblAdjustment = ToLong(row, 20),
    sblBalance = ToLong(row, 21),
    sblQuota = ToLong(row, 22),
};

// Google Sheets 讀回來的儲存格型別不固定(字串/double/long/int 都可能)，這裡統一轉換，
// 空字串視為 null(對應 DealerSelf/DealerHedge 這兩組上櫃沒有的欄位)。
static long? ToNullableLong(IList<object> row, int index)
{
    if (index >= row.Count)
    {
        return null;
    }
    return row[index] switch
    {
        null => null,
        string s when string.IsNullOrWhiteSpace(s) => null,
        string s when long.TryParse(s, out var l) => l,
        double d => (long)Math.Round(d),
        long l => l,
        int i => i,
        _ => null,
    };
}

static long ToLong(IList<object> row, int index) => ToNullableLong(row, index) ?? 0;

static string ToStr(IList<object> row, int index) => index < row.Count ? row[index]?.ToString()?.Trim() ?? "" : "";

static async Task BackfillNewStocksAsync(
    List<string> watchlist,
    WorkerApiClient api,
    TwseClient twse,
    TpexClient tpex,
    TwseMarginClient twseMargin,
    TpexMarginClient tpexMargin,
    ForeignShareholdingClient foreignShareholding,
    DateOnly historyStartDate)
{
    var institutionalEarliest = await api.GetEarliestDateByCodeAsync("institutional");
    var marginEarliest = await api.GetEarliestDateByCodeAsync("margin");
    var foreignShareholdingEarliest = await api.GetEarliestDateByCodeAsync("foreignShareholding");

    // 緩衝 14 天，避免把「本來就從 1 月中才有資料的正常股票」誤判成新股票
    // (年初這幾天常常剛好遇到假日，資料本來就不會剛好從歷史起始日當天開始)。
    var threshold = historyStartDate.AddDays(14);
    bool NeedsBackfill(Dictionary<string, DateOnly> earliestByCode, string code) =>
        !earliestByCode.TryGetValue(code, out var earliest) || earliest > threshold;

    // 三份資料表各自獨立判斷，例如剛新增「外資持股比率」這份表時，既有股票的三大法人資料
    // 已經齊全不需要回補，但這份表是全新的，仍然需要幫既有股票補歷史資料。
    var needsInstitutional = watchlist.Where(c => NeedsBackfill(institutionalEarliest, c)).ToList();
    var needsMargin = watchlist.Where(c => NeedsBackfill(marginEarliest, c)).ToList();
    var needsForeignShareholding = watchlist.Where(c => NeedsBackfill(foreignShareholdingEarliest, c)).ToList();
    var stocksNeedingAny = needsInstitutional.Union(needsMargin).Union(needsForeignShareholding).ToList();

    if (stocksNeedingAny.Count == 0)
    {
        return;
    }

    Console.WriteLine($"偵測到 {stocksNeedingAny.Count} 檔股票缺少從 {historyStartDate:yyyy-MM-dd} 開始的歷史資料，自動回補中：{string.Join(", ", stocksNeedingAny)}");
    Console.WriteLine($"  三大法人：{string.Join(", ", needsInstitutional)}");
    Console.WriteLine($"  融資融券：{string.Join(", ", needsMargin)}");
    Console.WriteLine($"  外資持股比率：{string.Join(", ", needsForeignShareholding)}");

    // 刻意從「昨天」往回補到歷史起始日，而不是從起始日往前補：這樣萬一這次執行被 TWSE 中斷，
    // 已經補到的都是「比較新」的日期，GetEarliestDateByCodeAsync 抓到的最早日期依然會比門檻晚，
    // 下次排程會自然判斷「還沒補完」並從昨天重新往回接著補，不會卡在同一個地方一直重來。
    var yesterday = DateOnly.FromDateTime(DateTime.Today).AddDays(-1);
    var backfillDates = new List<DateOnly>();
    for (var d = yesterday; d >= historyStartDate; d = d.AddDays(-1))
    {
        backfillDates.Add(d);
    }

    // 回補是一次要打上百次 TWSE/TPEx 請求，比平常的 5 天滾動視窗密集很多，
    // 曾經實測觸發過 TWSE 的防濫用機制(回傳 307 導向到封鎖頁面)。
    // 間隔拉長一點降低觸發機會；一旦真的偵測到封鎖，整批回補立刻中止，
    // 不要繼續對已經封鎖自己的來源送更多請求，留到下次排程再繼續。
    const int backfillDelayMs = 1000;

    // 以「日期」為主迴圈、股票為次，而不是「股票」為主迴圈：TWSE/TPEx 這幾支 API 本來就是回傳
    // 整個市場當天的資料，過去逐股票分開打，等於同一天的整包市場資料被重複抓了 N 次(N=待補股票數)，
    // 既浪費又更容易觸發 TWSE 防濫用機制。改成同一天要補的股票一次打包處理，不管有幾支股票在排隊，
    // 每天對 TWSE/TPEx 的請求數都是固定的。這樣做還有個好處：萬一真的被封鎖，所有待補股票都已經
    // 公平地補到同樣深的日期，不會像過去那樣「前面的股票補到一半，後面排隊的股票完全沒被碰到」。
    foreach (var targetDate in backfillDates)
    {
        if (needsInstitutional.Count > 0)
        {
            try
            {
                await ProcessInstitutionalDateAsync(targetDate, needsInstitutional, api, twse, tpex);
            }
            catch (Exception ex) when (IsTwseBlocked(ex))
            {
                Console.WriteLine("警告：TWSE 疑似暫時封鎖本次執行的來源(收到 307 導向)，立即停止本次歷史回補，留到下次排程接著補。");
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"警告：{targetDate:yyyy-MM-dd} 三大法人回補處理失敗，跳過並繼續下一天。錯誤：{ex.Message}");
            }
        }

        if (needsMargin.Count > 0)
        {
            try
            {
                await ProcessMarginDateAsync(targetDate, needsMargin, api, twseMargin, tpexMargin);
            }
            catch (Exception ex) when (IsTwseBlocked(ex))
            {
                Console.WriteLine("警告：TWSE 疑似暫時封鎖本次執行的來源(收到 307 導向)，立即停止本次歷史回補，留到下次排程接著補。");
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"警告：{targetDate:yyyy-MM-dd} 融資融券回補處理失敗，跳過並繼續下一天。錯誤：{ex.Message}");
            }
        }

        if (needsForeignShareholding.Count > 0)
        {
            try
            {
                await ProcessForeignShareholdingDateAsync(targetDate, needsForeignShareholding, api, foreignShareholding);
            }
            catch (Exception ex) when (IsTwseBlocked(ex))
            {
                Console.WriteLine("警告：TWSE 疑似暫時封鎖本次執行的來源(收到 307 導向)，立即停止本次歷史回補，留到下次排程接著補。");
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"警告：{targetDate:yyyy-MM-dd} 外資持股比率回補處理失敗，跳過並繼續下一天。錯誤：{ex.Message}");
            }
        }

        await Task.Delay(backfillDelayMs);
    }

    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 歷史回補完成。");
}

// TWSE 對短時間內大量請求的來源會回傳 307 導向到封鎖頁面(而非單純的錯誤狀態碼)，
// 這是判斷「已經被暫時封鎖」的訊號，跟其他偶發性的格式錯誤要分開處理。
static bool IsTwseBlocked(Exception ex) =>
    ex is HttpRequestException httpEx && httpEx.StatusCode == System.Net.HttpStatusCode.TemporaryRedirect;

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

static async Task ProcessInstitutionalDateAsync(
    DateOnly targetDate,
    List<string> watchlist,
    WorkerApiClient api,
    TwseClient twse,
    TpexClient tpex)
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 開始處理 {targetDate:yyyy-MM-dd} 的三大法人買賣超資料...");

    var (listedTrades, otcTrades) = await FetchInstitutionalWithRetryAsync(targetDate, watchlist, twse, tpex);

    if (listedTrades.Count == 0 && otcTrades.Count == 0)
    {
        Console.WriteLine($"{targetDate:yyyy-MM-dd} 三大法人上市、上櫃都沒有資料，可能是假日或非交易日，跳過。");
        return;
    }

    var details = new List<InstitutionalTradeDetail>();
    foreach (var code in watchlist)
    {
        if (listedTrades.TryGetValue(code, out var listedDetail))
        {
            details.Add(listedDetail);
        }
        else if (otcTrades.TryGetValue(code, out var otcDetail))
        {
            details.Add(otcDetail);
        }
        else
        {
            Console.WriteLine($"警告：{targetDate:yyyy-MM-dd} 三大法人上市、上櫃都找不到股票代號 {code} 的資料，請確認代號是否正確，或該股票當天無交易。");
        }
    }

    if (details.Count == 0)
    {
        Console.WriteLine($"{targetDate:yyyy-MM-dd} 三大法人沒有任何一檔股票有資料可寫入。");
        return;
    }

    var (added, updated, unchanged) = await api.UpsertInstitutionalAsync(targetDate, details);
    Console.WriteLine($"{targetDate:yyyy-MM-dd} 三大法人：新增 {added} 筆、更新 {updated} 筆、未變 {unchanged} 筆。");
}

// TWSE/TPEx 偶爾會「技術上成功回應，但剛好缺 Watchlist 裡某幾檔股票的資料」，這種情況不會拋例外，
// 過去完全不會觸發重試(2026-07-09 那次資料短缺就是這個原因：兩邊都有回應、只是缺了幾檔股票，
// 排程當下沒有再試一次的機制)。這裡在「兩邊都有回應、但仍缺代號」時額外重試幾次。
static async Task<(Dictionary<string, InstitutionalTradeDetail> Listed, Dictionary<string, InstitutionalTradeDetail> Otc)> FetchInstitutionalWithRetryAsync(
    DateOnly targetDate, List<string> watchlist, TwseClient twse, TpexClient tpex)
{
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

    const int maxMissingRetries = 2;
    for (var attempt = 0; attempt < maxMissingRetries; attempt++)
    {
        if (listedTrades.Count == 0 && otcTrades.Count == 0)
        {
            break; // 非交易日，不用因為缺代號而重試
        }

        var missing = watchlist.Where(c => !listedTrades.ContainsKey(c) && !otcTrades.ContainsKey(c)).ToList();
        if (missing.Count == 0)
        {
            break;
        }

        Console.WriteLine($"警告：{targetDate:yyyy-MM-dd} 三大法人有 {missing.Count} 檔股票查無資料({string.Join(", ", missing)})，等待後重試...");
        await Task.Delay(3000);
        listedTrades = await twse.GetInstitutionalTradesAsync(targetDate);
        otcTrades = await tpex.GetInstitutionalTradesAsync(targetDate);
    }

    return (listedTrades, otcTrades);
}

static async Task ProcessMarginDateAsync(
    DateOnly targetDate,
    List<string> watchlist,
    WorkerApiClient api,
    TwseMarginClient twseMargin,
    TpexMarginClient tpexMargin)
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 開始處理 {targetDate:yyyy-MM-dd} 的融資融券借券資料...");

    var (listedMargin, otcMargin) = await FetchMarginWithRetryAsync(targetDate, watchlist, twseMargin, tpexMargin);

    if (listedMargin.Count == 0 && otcMargin.Count == 0)
    {
        Console.WriteLine($"{targetDate:yyyy-MM-dd} 融資融券上市、上櫃都沒有資料，可能是假日或非交易日，跳過。");
        return;
    }

    var details = new List<MarginTradingDetail>();
    foreach (var code in watchlist)
    {
        if (listedMargin.TryGetValue(code, out var listedDetail))
        {
            details.Add(listedDetail);
        }
        else if (otcMargin.TryGetValue(code, out var otcDetail))
        {
            details.Add(otcDetail);
        }
        else
        {
            Console.WriteLine($"警告：{targetDate:yyyy-MM-dd} 融資融券上市、上櫃都找不到股票代號 {code} 的資料，請確認代號是否正確，或該股票當天無交易。");
        }
    }

    if (details.Count == 0)
    {
        Console.WriteLine($"{targetDate:yyyy-MM-dd} 融資融券沒有任何一檔股票有資料可寫入。");
        return;
    }

    var (added, updated, unchanged) = await api.UpsertMarginAsync(targetDate, details);
    Console.WriteLine($"{targetDate:yyyy-MM-dd} 融資融券：新增 {added} 筆、更新 {updated} 筆、未變 {unchanged} 筆。");
}

static async Task<(Dictionary<string, MarginTradingDetail> Listed, Dictionary<string, MarginTradingDetail> Otc)> FetchMarginWithRetryAsync(
    DateOnly targetDate, List<string> watchlist, TwseMarginClient twseMargin, TpexMarginClient tpexMargin)
{
    Dictionary<string, MarginTradingDetail> listedMargin;
    try
    {
        listedMargin = await twseMargin.GetMarginTradingAsync(targetDate);
    }
    catch (Exception ex)
    {
        // TWSE 偶爾會回傳格式異常的暫時性錯誤回應，等一下重試一次，通常就會拿到正常資料。
        Console.WriteLine($"警告：{targetDate:yyyy-MM-dd} TWSE 融資融券回應異常({ex.Message})，等待後重試一次...");
        await Task.Delay(2000);
        listedMargin = await twseMargin.GetMarginTradingAsync(targetDate);
    }

    var otcMargin = await tpexMargin.GetMarginTradingAsync(targetDate);

    const int maxMissingRetries = 2;
    for (var attempt = 0; attempt < maxMissingRetries; attempt++)
    {
        if (listedMargin.Count == 0 && otcMargin.Count == 0)
        {
            break;
        }

        var missing = watchlist.Where(c => !listedMargin.ContainsKey(c) && !otcMargin.ContainsKey(c)).ToList();
        if (missing.Count == 0)
        {
            break;
        }

        Console.WriteLine($"警告：{targetDate:yyyy-MM-dd} 融資融券有 {missing.Count} 檔股票查無資料({string.Join(", ", missing)})，等待後重試...");
        await Task.Delay(3000);
        listedMargin = await twseMargin.GetMarginTradingAsync(targetDate);
        otcMargin = await tpexMargin.GetMarginTradingAsync(targetDate);
    }

    return (listedMargin, otcMargin);
}

static async Task ProcessForeignShareholdingDateAsync(
    DateOnly targetDate,
    List<string> watchlist,
    WorkerApiClient api,
    ForeignShareholdingClient foreignShareholding)
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 開始處理 {targetDate:yyyy-MM-dd} 的外資持股比率資料...");

    var shareholding = await FetchForeignShareholdingWithRetryAsync(targetDate, watchlist, foreignShareholding);

    if (shareholding.Count == 0)
    {
        Console.WriteLine($"{targetDate:yyyy-MM-dd} 外資持股比率沒有資料，可能是假日或非交易日，跳過。");
        return;
    }

    var details = new List<ForeignShareholdingDetail>();
    foreach (var code in watchlist)
    {
        if (shareholding.TryGetValue(code, out var detail))
        {
            details.Add(detail);
        }
        else
        {
            Console.WriteLine($"警告：{targetDate:yyyy-MM-dd} 外資持股比率找不到股票代號 {code} 的資料，請確認代號是否正確，或該股票當天無交易。");
        }
    }

    if (details.Count == 0)
    {
        Console.WriteLine($"{targetDate:yyyy-MM-dd} 外資持股比率沒有任何一檔股票有資料可寫入。");
        return;
    }

    var (added, updated, unchanged) = await api.UpsertForeignShareholdingAsync(targetDate, details);
    Console.WriteLine($"{targetDate:yyyy-MM-dd} 外資持股比率：新增 {added} 筆、更新 {updated} 筆、未變 {unchanged} 筆。");
}

static async Task<Dictionary<string, ForeignShareholdingDetail>> FetchForeignShareholdingWithRetryAsync(
    DateOnly targetDate, List<string> watchlist, ForeignShareholdingClient foreignShareholding)
{
    var shareholding = await foreignShareholding.GetForeignShareholdingAsync(targetDate);

    const int maxMissingRetries = 2;
    for (var attempt = 0; attempt < maxMissingRetries; attempt++)
    {
        if (shareholding.Count == 0)
        {
            break; // 非交易日，不用因為缺代號而重試
        }

        var missing = watchlist.Where(c => !shareholding.ContainsKey(c)).ToList();
        if (missing.Count == 0)
        {
            break;
        }

        Console.WriteLine($"警告：{targetDate:yyyy-MM-dd} 外資持股比率有 {missing.Count} 檔股票查無資料({string.Join(", ", missing)})，等待後重試...");
        await Task.Delay(3000);
        shareholding = await foreignShareholding.GetForeignShareholdingAsync(targetDate);
    }

    return shareholding;
}
