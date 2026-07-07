# PPI.Stock.Fetcher

每日抓取台股（上市 TWSE + 上櫃 TPEx）三大法人買賣超明細資料，寫入 Google Sheets，供 Looker Studio 呈現圖表。

## 專案結構

```
PPI.Stock/
  PPI.Stock.sln
  src/PPI.Stock.Fetcher/
    Program.cs                   進入點：讀清單 -> 分別打 TWSE/TPEx -> 寫入 Sheet
    TwseClient.cs                 上市股票，呼叫 TWSE T86 API（支援任意歷史日期回補）
    TpexClient.cs                 上櫃股票，呼叫 TPEx OpenAPI（只能抓最新一天，不支援回補）
    InstitutionalTradeDetail.cs   三大法人買賣超資料模型
    GoogleSheetsClient.cs         讀寫 Google Sheet
    GoogleSheetsSettings.cs       設定物件
    appsettings.json              設定檔(需要你填入兩份試算表各自的 SpreadsheetId)
```

程式會針對 Watchlist 裡的每個股票代號，先查 TWSE(上市)資料，查不到再查 TPEx(上櫃)，兩邊都查不到才會顯示警告。**不需要**在清單裡註明是上市還是上櫃，程式會自動判斷。

Google Sheet(試算表)、分頁名稱、欄位標題都採用全英文命名（因為主要是給 Looker Studio 讀取顯示用），中文說明統一寫在這份 README，方便查閱。

**Watchlist 跟 InstitutionalTrades 是兩份各自獨立的試算表(不同 SpreadsheetId)，不是同一份試算表裡的兩個分頁。** 這樣設計是因為 Watchlist 屬於跨主題共用的股票主檔，以後如果要加融資融券之類的新主題，可以直接讀同一份 Watchlist，不用重複維護股票清單；InstitutionalTrades 則是三大法人買賣超這個主題專屬的資料。

## Sheet 結構 / 欄位對照表

### 1. `Watchlist` 試算表（股票觀察清單，跨主題共用的主檔資料）

| 欄位 (Column) | 說明 |
|---|---|
| A: Code | 股票代號，程式只會讀這一欄，上市上櫃都可直接填 |
| B: ShortName | 簡稱（自由填寫，純顯示用，程式不會讀取） |
| C: FullName | 全名（自由填寫，純顯示用，程式不會讀取） |

A 欄從第 2 列開始，每列一檔股票。

### 2. `InstitutionalTrades` 試算表（每日三大法人買賣超明細，三大法人主題專屬）

「InstitutionalTrades」分頁每一列(row)有 26 個欄位(A~Z)，第 1 列請貼上這行英文標題：

```
Date	Market	StockCode	StockName	ForeignExDealerBuy	ForeignExDealerSell	ForeignExDealerNet	ForeignDealerBuy	ForeignDealerSell	ForeignDealerNet	ForeignTotalBuy	ForeignTotalSell	ForeignTotalNet	TrustBuy	TrustSell	TrustNet	DealerSelfBuy	DealerSelfSell	DealerSelfNet	DealerHedgeBuy	DealerHedgeSell	DealerHedgeNet	DealerTotalBuy	DealerTotalSell	DealerTotalNet	GrandTotalNet
```

各欄位中文對照：

| 英文欄位 (Column) | 中文說明 |
|---|---|
| Date | 日期 |
| Market | 市場別，值為 `Listed`(上市) 或 `OTC`(上櫃) |
| StockCode | 股票代號 |
| StockName | 股票名稱（TWSE/TPEx 官方回傳的名稱） |
| ForeignExDealerBuy / Sell / Net | 外陸資(不含外資自營商)-買進/賣出/買賣超 |
| ForeignDealerBuy / Sell / Net | 外資自營商-買進/賣出/買賣超 |
| ForeignTotalBuy / Sell / Net | 外資合計(上兩者相加)-買進/賣出/買賣超 |
| TrustBuy / Sell / Net | 投信-買進/賣出/買賣超 |
| DealerSelfBuy / Sell / Net | 自營商-自行買賣-買進/賣出/買賣超（**上櫃無此資料，會是空白**） |
| DealerHedgeBuy / Sell / Net | 自營商-避險-買進/賣出/買賣超（**上櫃無此資料，會是空白**） |
| DealerTotalBuy / Sell / Net | 自營商合計-買進/賣出/買賣超 |
| GrandTotalNet | 三大法人合計買賣超淨額 |

**重要限制**：上櫃(TPEx)股票的自營商資料官方沒有拆分「自行買賣」跟「避險」，只有合計數字，所以上櫃股票的 `DealerSelf*` / `DealerHedge*` 欄位(Q~V欄)會是空白，只有上市股票才有完整拆分。

## 使用前的手動設定（Google 端）

1. **建立兩份獨立的 Google Sheet**（不是同一份試算表的兩個分頁）：
   - 一份叫 `Watchlist`，裡面的分頁也叫 `Watchlist`，欄位見上方對照表。
   - 一份叫 `InstitutionalTrades`，裡面的分頁也叫 `InstitutionalTrades`，第 1 列建議貼上方的英文標題列，程式從第 2 列開始 append。

2. **建立 Google Cloud 服務帳號**（用來讓程式免登入直接存取這兩份 Sheet）：
   - 到 [Google Cloud Console](https://console.cloud.google.com/) 建立一個新專案。
   - 左側選單「API 和服務」→「已啟用的 API 和服務」→ 啟用 **Google Sheets API**。
   - 「API 和服務」→「憑證」→「建立憑證」→「服務帳號」，建立完成後進入該服務帳號，「金鑰」分頁 →「新增金鑰」→ JSON，會下載一個 `.json` 檔。
   - 把這個 JSON 檔放到 `src/PPI.Stock.Fetcher/credentials/service-account.json`（`credentials` 資料夾已被 `.gitignore` 排除，不會被上傳）。

3. **把兩份 Google Sheet 都分享給同一個服務帳號**：
   - 打開剛剛下載的 JSON 檔，找到 `client_email` 欄位（類似 `xxx@xxx.iam.gserviceaccount.com`）。
   - 分別打開 `Watchlist` 和 `InstitutionalTrades` 這兩份試算表，點右上角「共用」，把這個 email 加進去，權限設為「編輯者」。**兩份都要分享，缺一份程式就會讀不到/寫不進去。**

4. **填寫 `appsettings.json`**：
   - `WatchlistSpreadsheetId`：`Watchlist` 試算表網址中 `/d/` 和 `/edit` 之間那一段亂碼。
   - `DataSpreadsheetId`：`InstitutionalTrades` 試算表網址中 `/d/` 和 `/edit` 之間那一段亂碼。
   - 確認 `WatchlistSheetName`、`DataSheetName` 跟你實際的分頁名稱一致。

未來新增融資融券等主題時，只要在該主題的新試算表設定裡沿用同一個 `WatchlistSpreadsheetId`，不用重複建立股票清單，也不用重新跑一次服務帳號授權流程。

## 執行方式

```powershell
cd src/PPI.Stock.Fetcher
dotnet run
```

不帶參數預設抓「今天」的資料。若要補抓過去某一天（**僅上市股票有效**，上櫃固定只能抓最新一天），可帶日期參數：

```powershell
dotnet run -- 20260703
```

若當天是假日/非交易日，TWSE/TPEx 都不會有資料，程式會直接跳過、不寫入。

### 只回補特定股票的歷史資料

如果 Watchlist 裡已經有股票在跑，之後才新增一支新股票，想單獨回補這支新股票從某天開始的歷史資料，可以帶第二個參數指定股票代號，避免整個清單重跑導致其他股票的資料重複寫入：

```powershell
dotnet run -- 20260703 3209
```

這樣只會處理 `3209` 這支股票，其他已經在 Watchlist 裡的股票不會受影響。

## 排程執行

TWSE 資料通常下午 3 點多才會更新完成，建議排在下午 4 點後執行。用 Windows工作排程器 建立每日排程，動作設定為：

- 程式：`dotnet`
- 引數：`run --project "D:\ClaudeCode\PPI.Stock\src\PPI.Stock.Fetcher"`

或先 `dotnet publish` 產生執行檔後，直接排程呼叫該 exe，啟動速度會比 `dotnet run` 快。

## 之後要新增股票

不用改程式，直接到 `Watchlist` 試算表多加一列股票代號即可（上市上櫃都可以），隔天排程跑完就會自動開始記錄。
