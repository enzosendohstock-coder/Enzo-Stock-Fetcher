namespace PPI.Stock.Fetcher;

/// <summary>
/// 單一股票、單一交易日的外資及陸資持股比率明細。
/// 資料來自 TWSE/TPEx 官方每日公告(集保結算所登記的實際持股)，不是用買賣超累加估算出來的。
/// 只有外資/陸資因為有法定投資額度管制才有這種每日官方揭露，投信、自營商沒有對應資料來源。
/// </summary>
public class ForeignShareholdingDetail
{
    public required string StockCode { get; init; }
    public required string StockName { get; init; }
    public required Market Market { get; init; }

    public long IssuedShares { get; init; }       // 發行股數
    public long AvailableShares { get; init; }    // 外資及陸資尚可投資股數
    public long HeldShares { get; init; }         // 外資及陸資持有股數
    public double AvailableRatio { get; init; }   // 外資及陸資尚可投資比率(%)
    public double HeldRatio { get; init; }        // 外資及陸資持股比率(%)
}
