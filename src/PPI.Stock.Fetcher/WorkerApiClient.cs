using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace PPI.Stock.Fetcher;

/// <summary>
/// 呼叫 Cloudflare Worker 的讀寫端點，取代舊的 GoogleSheetsClient。所有寫入都帶 bearer token
/// 認證，Worker 那邊會核對 FETCHER_API_TOKEN 是否吻合。
/// </summary>
public class WorkerApiClient
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public WorkerApiClient(HttpClient httpClient, WorkerApiSettings settings)
    {
        _httpClient = httpClient;
        _baseUrl = settings.BaseUrl.TrimEnd('/');
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiToken);
    }

    public async Task<List<string>> GetWatchlistAsync()
    {
        var response = await _httpClient.GetFromJsonAsync<WatchlistResponse>($"{_baseUrl}/api/watchlist", JsonOptions);
        return response?.List?.Select(s => s.Code).ToList() ?? new List<string>();
    }

    public async Task<Dictionary<string, DateOnly>> GetEarliestDateByCodeAsync(string table)
    {
        var response = await _httpClient.GetFromJsonAsync<EarliestDateResponse>(
            $"{_baseUrl}/api/write/earliest-date?table={table}", JsonOptions);

        var result = new Dictionary<string, DateOnly>();
        if (response?.EarliestDates == null)
        {
            return result;
        }

        foreach (var (code, dateStr) in response.EarliestDates)
        {
            if (DateOnly.TryParse(dateStr, out var date))
            {
                result[code] = date;
            }
        }
        return result;
    }

    public Task<(int Added, int Updated, int Unchanged)> UpsertInstitutionalAsync(DateOnly date, IEnumerable<InstitutionalTradeDetail> details) =>
        PostUpsertAsync("/api/write/institutional", date, details.Select(ToRow));

    public Task<(int Added, int Updated, int Unchanged)> UpsertMarginAsync(DateOnly date, IEnumerable<MarginTradingDetail> details) =>
        PostUpsertAsync("/api/write/margin", date, details.Select(ToRow));

    public Task<(int Added, int Updated, int Unchanged)> UpsertForeignShareholdingAsync(DateOnly date, IEnumerable<ForeignShareholdingDetail> details) =>
        PostUpsertAsync("/api/write/foreign-shareholding", date, details.Select(ToRow));

    public Task<(int Added, int Updated, int Unchanged)> UpsertStockPriceAsync(DateOnly date, IEnumerable<StockPriceDetail> details) =>
        PostUpsertAsync("/api/write/stock-price", date, details.Select(ToRow));

    // 給一次性搬遷腳本用：搬遷是直接從 Google Sheet 的原始儲存格資料組 row，
    // 不需要先還原成完整的 InstitutionalTradeDetail/MarginTradingDetail 物件再轉一次。
    public Task<(int Added, int Updated, int Unchanged)> UpsertInstitutionalRawAsync(DateOnly date, IEnumerable<object> rows) =>
        PostUpsertAsync("/api/write/institutional", date, rows);

    public Task<(int Added, int Updated, int Unchanged)> UpsertMarginRawAsync(DateOnly date, IEnumerable<object> rows) =>
        PostUpsertAsync("/api/write/margin", date, rows);

    public async Task<bool> TryAddWatchlistEntryAsync(string code, string shortName, string fullName)
    {
        var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/api/watchlist", new
        {
            code,
            shortName,
            fullName,
            honeypot = "",
        }, JsonOptions);

        var result = await response.Content.ReadFromJsonAsync<WatchlistWriteResponse>(JsonOptions);
        return result?.Success ?? false;
    }

    private async Task<(int Added, int Updated, int Unchanged)> PostUpsertAsync(string path, DateOnly date, IEnumerable<object> rows)
    {
        var body = new { date = date.ToString("yyyy-MM-dd"), rows };
        var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}{path}", body, JsonOptions);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<UpsertResponse>(JsonOptions);
        return (result?.Added ?? 0, result?.Updated ?? 0, result?.Unchanged ?? 0);
    }

    private static object ToRow(InstitutionalTradeDetail d) => new
    {
        market = d.Market == Market.Listed ? "Listed" : "OTC",
        stockCode = d.StockCode,
        stockName = d.StockName,
        foreignExDealerBuy = d.ForeignExDealerBuy,
        foreignExDealerSell = d.ForeignExDealerSell,
        foreignExDealerNet = d.ForeignExDealerNet,
        foreignDealerBuy = d.ForeignDealerBuy,
        foreignDealerSell = d.ForeignDealerSell,
        foreignDealerNet = d.ForeignDealerNet,
        foreignTotalBuy = d.ForeignTotalBuy,
        foreignTotalSell = d.ForeignTotalSell,
        foreignTotalNet = d.ForeignTotalNet,
        trustBuy = d.TrustBuy,
        trustSell = d.TrustSell,
        trustNet = d.TrustNet,
        dealerSelfBuy = d.DealerSelfBuy,
        dealerSelfSell = d.DealerSelfSell,
        dealerSelfNet = d.DealerSelfNet,
        dealerHedgeBuy = d.DealerHedgeBuy,
        dealerHedgeSell = d.DealerHedgeSell,
        dealerHedgeNet = d.DealerHedgeNet,
        dealerTotalBuy = d.DealerTotalBuy,
        dealerTotalSell = d.DealerTotalSell,
        dealerTotalNet = d.DealerTotalNet,
        grandTotalNet = d.GrandTotalNet,
    };

    private static object ToRow(MarginTradingDetail d) => new
    {
        market = d.Market == Market.Listed ? "Listed" : "OTC",
        stockCode = d.StockCode,
        stockName = d.StockName,
        marginBuy = d.MarginBuy,
        marginSell = d.MarginSell,
        marginCashRedemption = d.MarginCashRedemption,
        marginBalancePrev = d.MarginBalancePrev,
        marginBalance = d.MarginBalance,
        marginQuota = d.MarginQuota,
        shortSell = d.ShortSell,
        shortBuy = d.ShortBuy,
        shortStockRedemption = d.ShortStockRedemption,
        shortBalancePrev = d.ShortBalancePrev,
        shortBalance = d.ShortBalance,
        shortQuota = d.ShortQuota,
        offsetting = d.Offsetting,
        sblBalancePrev = d.SblBalancePrev,
        sblSell = d.SblSell,
        sblReturn = d.SblReturn,
        sblAdjustment = d.SblAdjustment,
        sblBalance = d.SblBalance,
        sblQuota = d.SblQuota,
    };

    private static object ToRow(ForeignShareholdingDetail d) => new
    {
        market = d.Market == Market.Listed ? "Listed" : "OTC",
        stockCode = d.StockCode,
        stockName = d.StockName,
        issuedShares = d.IssuedShares,
        availableShares = d.AvailableShares,
        heldShares = d.HeldShares,
        availableRatio = d.AvailableRatio,
        heldRatio = d.HeldRatio,
    };

    private static object ToRow(StockPriceDetail d) => new
    {
        market = d.Market == Market.Listed ? "Listed" : "OTC",
        stockCode = d.StockCode,
        stockName = d.StockName,
        open = d.Open,
        high = d.High,
        low = d.Low,
        close = d.Close,
        change = d.Change,
        volume = d.Volume,
        transactionCount = d.TransactionCount,
        turnoverValue = d.TurnoverValue,
    };

    private class WatchlistResponse
    {
        public bool Success { get; set; }
        public List<WatchlistItem> List { get; set; } = new();
    }

    private class WatchlistItem
    {
        public string Code { get; set; } = "";
        public string ShortName { get; set; } = "";
        public string FullName { get; set; } = "";
    }

    private class WatchlistWriteResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
    }

    private class EarliestDateResponse
    {
        public bool Success { get; set; }
        public Dictionary<string, string> EarliestDates { get; set; } = new();
    }

    private class UpsertResponse
    {
        public bool Success { get; set; }
        public int Added { get; set; }
        public int Updated { get; set; }
        public int Unchanged { get; set; }
    }
}
