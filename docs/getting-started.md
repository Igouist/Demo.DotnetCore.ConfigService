# 設定分發 POC：短入門導讀

如果你看得懂基本 C# 與 JSON，但還不熟悉 .NET 設定機制，可以先讀這篇。本文先說明概念，再用一個設定鍵走過完整流程；不必先讀完設計規格。

本文的程式碼與回應用來說明行為，不是一份可整段貼上執行的腳本。實際操作請使用最後連結的示範手冊。

## 1. 這個專案在解決什麼問題？

假設兩個應用程式使用同一份設定值，卻各自把它放在本機設定檔。要修改這個值，就得分別修改兩個應用程式的設定。

這個 POC（概念驗證）改為在中央管理共享值。各應用程式自己宣告需要哪些設定，啟動時再取得中央值。

用一句話記住它：**中央管理一次，應用程式啟動時取得；啟動後不會自動更新。**

同一個中央環境與設定鍵只有一份共享中央值。若兩個應用程式需要不同的中央值，就應使用不同的設定鍵，而不是讓中央依應用程式身分偷偷換值。

## 2. 誰負責什麼？

- **中央設定主機（Central Config Host）**：讓操作人員設定、移除及查詢共享值，並保存版本歷史；也提供應用程式查詢設定的 HTTP 介面。
- **用戶端套件（Client package）**：在應用程式內執行，負責啟動時取得並驗證中央設定。它不是另一個獨立服務。
- **示範應用程式（Demo Consumer Host）**：使用套件，並提供 HTTP 端點，讓你觀察最後實際讀到的設定值。

需要哪些設定，由使用端應用程式自己決定。中央不保存應用程式登錄清單，也不替每個應用程式指派設定。

## 3. .NET 怎麼讀設定？

**設定鍵（Configuration Key）**就是設定的名稱。以本專案的本機設定片段為例：

```json
{
  "Demo": {
    "FallbackAllowedMessage": "local-fallback"
  }
}
```

巢狀名稱會以 `:` 連接，所以這個值的完整設定鍵是 `Demo:FallbackAllowedMessage`。

**設定提供者（Provider）**負責從某個來源提供設定，例如 JSON 檔、環境變數或中央快照。**`IConfiguration`** 則是應用程式讀取合併結果的介面。以下 C# 片段中的 `configuration` 代表應用程式已取得的 `IConfiguration`：

```csharp
string? message = configuration["Demo:FallbackAllowedMessage"];
```

若沒有其他來源提供同名設定，這裡讀到的是 `local-fallback`。若多個來源提供同一個設定鍵，就依來源優先序決定最後的值。這個最後讀到的結果稱為**有效設定**。

本專案把中央快照放在本機 JSON 設定之後，因此中央值可覆寫 JSON 值；User Secrets、環境變數與命令列仍可覆寫中央值。完整順序見 [README](../README.md)。

另外，別把兩種「環境」混在一起：**中央環境（Environment）**決定查哪一份中央資料；**主機環境（Host Environment）**決定 .NET 的本機執行行為與環境設定檔。兩者不必同名。

## 4. 跟著一個設定鍵走一遍

以下使用 `Demo:CentralRequiredMessage`，它已列在示範應用程式的 `CentralRequiredKeys` 中。

本專案的示範設定預設為 `CentralConfig:Enabled=false`，也就是只使用本機設定。下面的故事採用示範手冊的啟動條件：啟用中央載入，並選擇中央環境 `UAT`。所有值都是合成的示範資料。

### 第一步：把值放到中央

操作人員透過管理介面的 Set 路由，送出下列資料：

```json
{
  "environment": "UAT",
  "key": "Demo:CentralRequiredMessage",
  "value": "snapshot-v1"
}
```

**代表什麼？** 中央已有這個設定鍵的值。另一個應用程式若選擇相同中央環境與設定鍵，也會取得同一份中央值。

### 第二步：應用程式啟動時載入

在建立 .NET 主機之前，應用程式等待套件完成載入。以下是啟動順序片段：

```csharp
using Demo.DotnetCore.ConfigService.Client;

var builder = WebApplication.CreateBuilder(args);
await builder.AddCentralConfigAsync();
var app = builder.Build();
```

`builder` 用來準備應用程式的設定與服務；`Build()` 再建立主機。這裡的 `await` 表示必須等待中央載入完成，不能一邊載入、一邊讓應用程式開始使用尚未確定的設定。

**代表什麼？** 套件取得並驗證中央回應後，把值加入 .NET 設定來源。套件不是在每次處理業務 HTTP 請求時，都重新向中央查詢。

### 第三步：觀察實際讀到的值

假設中央沒有 `Demo:FallbackAllowedMessage` 的值，也沒有較高優先序的覆寫，示範端點 `GET /api/v1/demo/effective-configuration` 會回傳：

```json
{
  "scope": "Demo",
  "effectiveValues": {
    "CentralRequiredMessage": "snapshot-v1",
    "FallbackAllowedMessage": "local-fallback",
    "LocalOnlyMessage": "local-only"
  }
}
```

**代表什麼？** `CentralRequiredMessage` 來自中央；另外兩個值來自本機 JSON。端點只呈現 `Demo` 區段，所以回應中的名稱省略 `Demo:`，不是換了設定鍵。

若在示範程序啟動時提供環境變數 `Demo__CentralRequiredMessage=environment-value`，有效值就會變成 `environment-value`；環境變數名稱中的 `__` 對應設定鍵中的 `:`。若再以命令列提供 `--Demo:CentralRequiredMessage command-value`，命令列會勝出。

**代表什麼？**「中央有值」不等於「最後一定使用中央值」。中央必要設定鍵仍須在中央存在，即使較高優先序的本機來源最後覆寫了它。

### 第四步：修改中央值，再重新啟動

先回到沒有覆寫的情況。應用程式啟動時取得的 `snapshot-v1`，會保存在不可變的**啟動快照（Startup snapshot）**中。快照可以理解成啟動時取得的一份設定副本。

| 時點 | 中央值 | 示範應用程式讀到的值 |
| --- | --- | --- |
| 啟動應用程式 | `snapshot-v1` | `snapshot-v1` |
| 將中央值改為 `snapshot-v2`，應用程式仍在執行 | `snapshot-v2` | `snapshot-v1` |
| 重新啟動應用程式 | `snapshot-v2` | `snapshot-v2` |

**代表什麼？** 這個 POC 驗證的是啟動時載入，不是即時更新。固定的是中央快照，不是所有 .NET 設定來源。

中央的每次 Set 都新增版本，不會抹掉舊值的歷史。`snapshot-v1` 與 `snapshot-v2` 只是示範字串，不是伺服器產生的版本號。

## 5. 缺值，不等於中央服務故障

使用端用兩個陣列宣告**設定鍵選擇（Key Selection）**：`CentralRequiredKeys` 表示必須在中央有值；`FallbackAllowedKeys` 表示中央缺值時，允許其他來源決定結果。

| 情況 | 套件的行為 |
| --- | --- |
| 中央回應有效，但缺少中央必要設定鍵 | 啟動失敗，即使本機有同名值也一樣。 |
| 中央回應有效，但缺少允許後備設定鍵 | 不加入該鍵的中央值，保留其他設定來源的結果；若其他來源也沒有值，該鍵仍缺少，由應用程式判斷是否可接受。 |
| 啟用中央載入，但無法取得有效中央回應 | 依有上限的重試與逾時策略處理；無法完成載入時，啟動失敗，不自動切換到本機設定。 |
| 明確設定 `CentralConfig:Enabled=false` | 不執行中央載入，刻意只使用本機設定。 |

因此，「允許後備」不是「中央連不上也能照常啟動」。空字串也不是缺值：中央存了 `""`，就代表它確實有值。

兩個選擇陣列只能宣告在基底 `appsettings.json`。不要把它們放到環境設定檔或用環境變數覆寫，因為 .NET 會按陣列索引合併，不保證整個陣列被替換。需要了解原因時，再讀[陣列合併範例](knowledge/dotnet-configuration-array-merging.md)。

## 6. 接下來怎麼讀、怎麼示範？

先依這條路線閱讀即可：

1. 回到 [README](../README.md)，對照架構圖、專案分工與必要工具。
2. 閱讀 [Client 使用說明](../src/Demo.DotnetCore.ConfigService.Client/README.md)，確認完整設定格式及呼叫方式。
3. 使用 [POC 示範手冊](demo/config-distribution-poc-runbook.md)，在隔離環境實際驗證上述行為。需要指定的 .NET SDK 與 PowerShell 7 以上版本；只使用非敏感資料。
4. 想知道設計理由，再讀 [共享中央值](adr/0001-share-value-by-environment-and-key.md)與[使用端擁有設定鍵選擇](adr/0002-consumer-owned-key-selection.md)兩份 ADR（架構決策紀錄）。

示範手冊會封裝套件、啟動程序、驗證結果並清理本次建立的資料，所以比這份導讀長。請依手冊執行完整區塊，不要為了逐步教學而拆開 `try`／`finally` 的清理流程。POC 端點沒有身分驗證；第一次示範不需要呼叫 `unsafe-root` 診斷端點，在提示時不要輸入 `YES` 即可略過。

完整設計、Readiness（就緒探針）、E2E（端對端測試）、HTTP 限制與 .NET Framework 相容性，都是後續按需查閱的資料，不必在第一次閱讀時全部掌握。文件入口見 [README 的維護與文件](../README.md#維護與文件)。
