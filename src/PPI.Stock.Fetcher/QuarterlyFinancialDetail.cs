namespace PPI.Stock.Fetcher;

/// <summary>
/// 單一股票、單一年季的季報四率(毛利率/營業利益率/稅前純益率/稅後純益率) + EPS + 損益表金額。
///
/// 金額欄位(Revenue ~ NetIncome)只有一般業(IndustryCategory="ci")損益表結構齊全；
/// 證期業/金控業/保險業/異業(bd/fh/ins/mim)的損益表科目名稱完全不同(例如金控業沒有「營業成本/毛利」
/// 這種概念，是用利息淨收益等完全不同的科目)，這幾個變體目前只會有 IndustryCategory + Eps，
/// 其餘金額欄位是 null，不是抓漏，是那個產業別本來就沒有對應科目。
/// GrossMargin/OperatingMargin/PretaxMargin/NetMargin 四個%是官方「營益分析彙總表」原始欄位，
/// 官方本身這份報表目前只涵蓋一般業，其餘產業別這四個%也會是 null。
/// </summary>
public class QuarterlyFinancialDetail
{
    public required string StockCode { get; init; }
    public required string StockName { get; init; }
    public required Market Market { get; init; }
    public required string IndustryCategory { get; init; }   // ci/bd/fh/ins/mim

    public long? Revenue { get; init; }                      // 營業收入
    public long? CostOfRevenue { get; init; }                // 營業成本
    public long? GrossProfit { get; init; }                  // 營業毛利（毛損）淨額
    public long? OperatingExpenses { get; init; }             // 營業費用
    public long? OperatingIncome { get; init; }               // 營業利益（損失）
    public long? NonOperatingIncomeExpenses { get; init; }    // 營業外收入及支出
    public long? PretaxIncome { get; init; }                  // 稅前淨利（淨損）
    public long? IncomeTaxExpense { get; init; }              // 所得稅費用（利益）
    public long? NetIncome { get; init; }                     // 本期淨利（淨損）
    public double? Eps { get; init; }                         // 基本每股盈餘(當季，元)

    public double? GrossMargin { get; init; }         // 毛利率(%)
    public double? OperatingMargin { get; init; }     // 營業利益率(%)
    public double? PretaxMargin { get; init; }        // 稅前純益率(%)
    public double? NetMargin { get; init; }           // 稅後純益率(%)
}
