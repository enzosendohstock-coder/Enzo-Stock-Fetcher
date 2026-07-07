namespace PPI.Stock.Fetcher;

public enum Market
{
    Listed,   // 上市 TWSE
    Otc,      // 上櫃 TPEx
}

/// <summary>
/// 單一股票、單一交易日的三大法人買賣超明細。
/// 自營商-自行買賣 / 自營商-避險 只有上市(TWSE)有提供拆分，上櫃(TPEx)只給合計，故為 nullable。
/// </summary>
public class InstitutionalTradeDetail
{
    public required string StockCode { get; init; }
    public required string StockName { get; init; }
    public required Market Market { get; init; }

    public long ForeignExDealerBuy { get; init; }   // 外陸資(不含外資自營商)-買進
    public long ForeignExDealerSell { get; init; }  // 外陸資(不含外資自營商)-賣出
    public long ForeignExDealerNet { get; init; }   // 外陸資(不含外資自營商)-買賣超

    public long ForeignDealerBuy { get; init; }      // 外資自營商-買進
    public long ForeignDealerSell { get; init; }     // 外資自營商-賣出
    public long ForeignDealerNet { get; init; }      // 外資自營商-買賣超

    public long ForeignTotalBuy { get; init; }       // 外資合計-買進
    public long ForeignTotalSell { get; init; }      // 外資合計-賣出
    public long ForeignTotalNet { get; init; }       // 外資合計-買賣超

    public long TrustBuy { get; init; }              // 投信-買進
    public long TrustSell { get; init; }             // 投信-賣出
    public long TrustNet { get; init; }              // 投信-買賣超

    public long? DealerSelfBuy { get; init; }        // 自營商-自行買賣-買進 (上櫃無)
    public long? DealerSelfSell { get; init; }       // 自營商-自行買賣-賣出 (上櫃無)
    public long? DealerSelfNet { get; init; }        // 自營商-自行買賣-買賣超 (上櫃無)

    public long? DealerHedgeBuy { get; init; }       // 自營商-避險-買進 (上櫃無)
    public long? DealerHedgeSell { get; init; }      // 自營商-避險-賣出 (上櫃無)
    public long? DealerHedgeNet { get; init; }       // 自營商-避險-買賣超 (上櫃無)

    public long DealerTotalBuy { get; init; }        // 自營商合計-買進
    public long DealerTotalSell { get; init; }       // 自營商合計-賣出
    public long DealerTotalNet { get; init; }        // 自營商合計-買賣超

    public long GrandTotalNet { get; init; }         // 三大法人合計買賣超

    /// <summary>
    /// 轉成寫入 Google Sheet「資料」分頁的一列(26 欄)，欄位順序需與 Sheet 標題列一致。
    /// </summary>
    public List<object> ToSheetRow(DateOnly date)
    {
        static object N(long? v) => v.HasValue ? v.Value : "";

        return new List<object>
        {
            date.ToString("yyyy-MM-dd"),
            Market == Market.Listed ? "Listed" : "OTC",
            StockCode,
            StockName,
            ForeignExDealerBuy, ForeignExDealerSell, ForeignExDealerNet,
            ForeignDealerBuy, ForeignDealerSell, ForeignDealerNet,
            ForeignTotalBuy, ForeignTotalSell, ForeignTotalNet,
            TrustBuy, TrustSell, TrustNet,
            N(DealerSelfBuy), N(DealerSelfSell), N(DealerSelfNet),
            N(DealerHedgeBuy), N(DealerHedgeSell), N(DealerHedgeNet),
            DealerTotalBuy, DealerTotalSell, DealerTotalNet,
            GrandTotalNet,
        };
    }
}
