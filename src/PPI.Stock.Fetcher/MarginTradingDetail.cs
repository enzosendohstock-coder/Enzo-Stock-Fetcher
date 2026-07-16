namespace PPI.Stock.Fetcher;

/// <summary>
/// 單一股票、單一交易日的融資融券 + 借券餘額明細。
/// 融資融券欄位來自 TWSE MI_MARGN / TPEx margin/balance(2026-07 起改用新端點)，
/// 借券餘額欄位來自 TWSE TWT93U / TPEx margin/sbl(2026-07 起改用新端點)。
/// 使用率(今日餘額/限額)不存欄位，交給前端算，跟三大法人買賣超的「張數」換算做法一致。
/// </summary>
public class MarginTradingDetail
{
    public required string StockCode { get; init; }
    public required string StockName { get; init; }
    public required Market Market { get; init; }

    public long MarginBuy { get; init; }             // 融資買進
    public long MarginSell { get; init; }            // 融資賣出
    public long MarginCashRedemption { get; init; }  // 融資現金償還
    public long MarginBalancePrev { get; init; }     // 融資前日餘額
    public long MarginBalance { get; init; }         // 融資今日餘額
    public long MarginQuota { get; init; }            // 融資限額

    public long ShortSell { get; init; }              // 融券賣出
    public long ShortBuy { get; init; }               // 融券買進(回補)
    public long ShortStockRedemption { get; init; }  // 融券現券償還
    public long ShortBalancePrev { get; init; }      // 融券前日餘額
    public long ShortBalance { get; init; }          // 融券今日餘額
    public long ShortQuota { get; init; }             // 融券限額

    public long Offsetting { get; init; }             // 資券相抵(當沖)

    public long SblBalancePrev { get; init; }        // 借券賣出前日餘額
    public long SblSell { get; init; }                // 借券當日賣出
    public long SblReturn { get; init; }              // 借券當日還券
    public long SblAdjustment { get; init; }          // 借券當日調整
    public long SblBalance { get; init; }             // 借券當日餘額(已借出、尚未歸還)
    public long SblQuota { get; init; }               // 借券次一營業日可限額

    /// <summary>
    /// 轉成寫入 Google Sheet「MarginTrading」分頁的一列(23 欄)，欄位順序需與 Sheet 標題列一致。
    /// </summary>
    public List<object> ToSheetRow(DateOnly date)
    {
        return new List<object>
        {
            date.ToString("yyyy-MM-dd"),
            Market == Market.Listed ? "Listed" : "OTC",
            StockCode,
            StockName,
            MarginBuy, MarginSell, MarginCashRedemption, MarginBalancePrev, MarginBalance, MarginQuota,
            ShortSell, ShortBuy, ShortStockRedemption, ShortBalancePrev, ShortBalance, ShortQuota,
            Offsetting,
            SblBalancePrev, SblSell, SblReturn, SblAdjustment, SblBalance, SblQuota,
        };
    }
}
