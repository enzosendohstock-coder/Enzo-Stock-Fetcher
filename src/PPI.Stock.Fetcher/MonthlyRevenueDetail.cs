namespace PPI.Stock.Fetcher;

/// <summary>
/// 單一股票、單一年月的月營收明細。MomPercent/YoyPercent/CumulativeYoyPercent 都是官方原始欄位，
/// 不是自己算的：比較基期為 0 時(例如新股剛掛牌)官方會留白，對應到這裡就是 null。
/// </summary>
public class MonthlyRevenueDetail
{
    public required string StockCode { get; init; }
    public required string StockName { get; init; }
    public required Market Market { get; init; }
    public required string Industry { get; init; }   // 產業別

    public long Revenue { get; init; }                    // 當月營收(千元)
    public long RevenuePrevMonth { get; init; }            // 上月營收
    public long RevenueLastYearMonth { get; init; }        // 去年當月營收
    public double? MomPercent { get; init; }               // 上月比較增減(%)
    public double? YoyPercent { get; init; }               // 去年同月增減(%)
    public long CumulativeRevenue { get; init; }           // 當月累計營收
    public long CumulativeRevenueLastYear { get; init; }   // 去年累計營收
    public double? CumulativeYoyPercent { get; init; }     // 累計前期比較增減(%)
}
