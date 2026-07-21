namespace PPI.Stock.Fetcher;

/// <summary>
/// 單一股票、單一交易日的收盤行情(OHLC)明細。
/// </summary>
public class StockPriceDetail
{
    public required string StockCode { get; init; }
    public required string StockName { get; init; }
    public required Market Market { get; init; }

    public double Open { get; init; }              // 開盤價
    public double High { get; init; }              // 最高價
    public double Low { get; init; }                // 最低價
    public double Close { get; init; }              // 收盤價
    public double Change { get; init; }             // 漲跌(有正負號)

    public long Volume { get; init; }               // 成交股數
    public long TransactionCount { get; init; }     // 成交筆數
    public long TurnoverValue { get; init; }        // 成交金額
}
