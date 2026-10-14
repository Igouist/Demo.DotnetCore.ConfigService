# .NET Configuration 的陣列與多 Provider 合併行為

本文說明 .NET 8 / ASP.NET Core Configuration 在多個 Configuration Provider 同時提供陣列資料時的實際行為，以及這個行為對 Config Distribution POC 的 `CentralRequiredKeys` 與 `FallbackAllowedKeys` 有何影響。本文只整理框架知識與可行做法，不替專案選擇方案。

## 結論先行

.NET Configuration 不會以「整個陣列」為單位進行覆蓋。每個 Configuration Provider 會先將自己的陣列攤平成帶有數字索引的完整 Key，系統再以完整 Key 為單位套用 Provider 優先順序。因此，較晚載入且長度較短的陣列只會覆蓋相同索引；較早來源中未被覆蓋的索引仍會保留。[Microsoft Learn 的多來源陣列範例](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/?view=aspnetcore-8.0#how-hierarchical-configuration-data-is-organized)直接展示了這項行為。

這個行為不宜簡稱為「陣列追加」。更精確的描述是：**陣列先被攤平，再按索引 Key 覆蓋**。

## Provider 優先順序是 Key 層級的優先順序

Configuration Provider 按加入 Configuration Pipeline 的順序讀取資料；多個 Provider 提供相同完整 Key 時，最後加入的 Provider 具有較高優先權。[Microsoft Learn 的 JSON Configuration Provider 說明](https://learn.microsoft.com/en-us/dotnet/core/extensions/configuration-providers#json-configuration-provider)示範先加入 `appsettings.json`，再加入 `appsettings.{Environment}.json`，並明確說明後讀取的 Provider 會覆蓋相同 Key。

ASP.NET Core 預設應用程式設定通常依序包含：

1. `appsettings.json`
2. `appsettings.{Environment}.json`
3. User Secrets
4. 環境變數
5. Command-line arguments

後面的來源對相同 Key 具有較高優先權；實際順序仍以應用程式如何建立 Configuration Pipeline 為準。[ASP.NET Core Configuration 文件](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/?view=aspnetcore-8.0#configuration-providers)同時說明了典型順序及自訂順序的能力。

這裡的關鍵是「相同完整 Key」：Provider 並不知道某個父節點在業務語意上是一個應整組取代的陣列。

## 陣列如何攤平成 Key

假設 `appsettings.json` 包含：

```json
{
  "CentralConfig": {
    "CentralRequiredKeys": [
      "A",
      "B",
      "C"
    ],
    "FallbackAllowedKeys": [
      "F1",
      "F2"
    ]
  }
}
```

JSON Configuration Provider 會將它表達為以下 Key-Value：

| 完整 Key | Value |
| --- | --- |
| `CentralConfig:CentralRequiredKeys:0` | `A` |
| `CentralConfig:CentralRequiredKeys:1` | `B` |
| `CentralConfig:CentralRequiredKeys:2` | `C` |
| `CentralConfig:FallbackAllowedKeys:0` | `F1` |
| `CentralConfig:FallbackAllowedKeys:1` | `F2` |

這不是概念上的類比，而是 JSON Provider 的資料模型。官方文件說明陣列索引會成為 Key 路徑的一部分；[`JsonConfigurationFileParser.VisitArrayElement`](https://github.com/dotnet/runtime/blob/main/src/libraries/Microsoft.Extensions.Configuration.Json/src/JsonConfigurationFileParser.cs#L52-L64)也可看到 Parser 從 `0` 開始，將每個索引加入目前的 Key 路徑。

## 完整範例：較短的環境陣列不會清掉舊索引

接續前面的 `appsettings.json`，假設 `appsettings.Production.json` 包含：

```json
{
  "CentralConfig": {
    "CentralRequiredKeys": [
      "X"
    ],
    "FallbackAllowedKeys": [
      "P1"
    ]
  }
}
```

這個 Provider 只提供：

| 完整 Key | Value |
| --- | --- |
| `CentralConfig:CentralRequiredKeys:0` | `X` |
| `CentralConfig:FallbackAllowedKeys:0` | `P1` |

`appsettings.Production.json` 較晚載入，所以索引 `0` 會被覆蓋。但它沒有提供索引 `1` 或 `2`，較早 Provider 的這些完整 Key 仍然存在。最終 Configuration 是：

| 最終完整 Key | 最終 Value | 值的來源 |
| --- | --- | --- |
| `CentralConfig:CentralRequiredKeys:0` | `X` | `appsettings.Production.json` |
| `CentralConfig:CentralRequiredKeys:1` | `B` | `appsettings.json` |
| `CentralConfig:CentralRequiredKeys:2` | `C` | `appsettings.json` |
| `CentralConfig:FallbackAllowedKeys:0` | `P1` | `appsettings.Production.json` |
| `CentralConfig:FallbackAllowedKeys:1` | `F2` | `appsettings.json` |

綁定後可觀察到的陣列為：

```text
CentralRequiredKeys = ["X", "B", "C"]
FallbackAllowedKeys = ["P1", "F2"]
```

這是「後置 Provider 只覆蓋相同完整 Key」與「各 Provider 分別由索引 `0` 攤平陣列」共同產生的結果。dotnet/runtime 的 [`ConfigurationRoot.GetConfiguration`](https://github.com/dotnet/runtime/blob/main/src/libraries/Microsoft.Extensions.Configuration/src/ConfigurationRoot.cs#L102-L114)會由最後一個 Provider 反向查找指定完整 Key；[`InternalConfigurationRootExtensions`](https://github.com/dotnet/runtime/blob/main/src/libraries/Microsoft.Extensions.Configuration/src/InternalConfigurationRootExtensions.cs#L19-L27)則會彙整各 Provider 的子 Key。因此，後置 Provider 沒有提供的索引仍可由較早 Provider 提供。

## 對本專案的影響

本專案使用兩個陣列宣告 Consumer Application 的 Key Selection：

- `CentralRequiredKeys`
- `FallbackAllowedKeys`

只要其中一個陣列可由多個 Provider 提供，就會套用相同的索引覆蓋行為。可能的結果包括：

- 工程師以為環境設定檔會整組取代，實際上卻保留基礎檔案尾端的 Key。
- 調整元素順序可能改變哪些項目被覆蓋，而不只是顯示順序。
- 兩個陣列分別合併後，可能產生重複 Key，或讓同一 Key 同時出現在 Required 與 Fallback Allowed 清單。
- 只查看環境設定檔不足以推導最終 Key Selection；還必須知道所有較低優先來源的內容與 Provider 順序。

以下三種做法都能形成一致的專案規則，但成本與能力不同。

### 做法一：禁止環境覆蓋 Key Selection 陣列

將兩個陣列視為應用程式本身的設定依賴，只在基礎 `appsettings.json` 宣告；Host Environment 專屬檔案不宣告這兩個陣列。其他啟動參數是否允許從較高優先來源覆蓋，可另外定義。

優勢：

- 避開索引合併，工程師只需查看一個位置即可理解完整 Key Selection。
- 各 Host Environment 使用同一組設定依賴，只改變 Configuration Entry 的值或中央 Environment。
- 不需要自訂集合合併演算法，也較容易讓未來的 .NET Framework 接收端維持相同概念。

需要處理的事項：

- 無法直接表達某個 Host Environment 特有的 Key Selection。
- 只寫在文件中屬於使用約定；若要在執行時強制阻止環境檔案、環境變數或 Command Line 提供陣列索引，必須檢查 Provider 來源，而不能只驗證合併後的 `IConfiguration`。
- ASP.NET Core 可透過 `IConfigurationRoot.Providers` 觀察已加入的 Provider，但如何判定與回報違規仍是專案自己的驗證規則。[Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/?view=aspnetcore-8.0#configuration-providers)提供了列出 Provider 的方式。

### 做法二：接受 .NET 原生的索引合併

允許任何具有較高優先權的 Provider 覆蓋陣列索引，並直接以合併後的 `IConfiguration` 建立 Key Selection。

優勢：

- 不需要額外的合併實作，行為與 .NET Configuration 的其他 Key 一致。
- Environment、User Secrets、環境變數與 Command Line 都能覆蓋指定索引。

需要處理的事項：

- 必須清楚文件化「索引覆蓋」而非「整組取代」，並在導入教材中提供實際範例。
- 陣列長度與順序都會影響結果，設定維護者必須同時理解各個 Provider。
- 套件應考慮在啟動時輸出最終正規化後的 Key Selection，並驗證重複 Key、兩個陣列交集及無效 Key，避免不直觀的合併結果靜默生效。
- 若想從較高優先來源清除較低來源的尾端索引，不能只提供較短陣列；必須另行設計表示方式。

### 做法三：自行實作「整組取代」

定義一套不同於原生索引合併的規則，例如：只要最高優先 Provider 宣告某個 Key Selection 陣列，就完全忽略較低優先 Provider 對該陣列提供的所有索引。

優勢：

- 符合多數人對環境設定檔覆蓋陣列的直覺。
- 可以讓各 Host Environment 擁有不同的完整 Key Selection，而不殘留基礎陣列尾端元素。

需要處理的事項：

- 套件不能只對合併完成的 `IConfiguration` 做一般陣列綁定，而需要辨識 Provider 優先順序及每個 Provider 提供的子 Key。
- 必須明確定義空陣列、稀疏索引、只有單一索引的環境變數，以及同一陣列由多種高優先來源局部提供時的語意。
- 需要涵蓋 `appsettings.json`、環境設定檔、User Secrets、環境變數、Command Line 與自訂 Provider 的測試矩陣。
- 未來 .NET Framework 4.8 接收端若使用不同的設定整合方式，也必須另外驗證並維持相同語意。

Microsoft Learn 描述的是原生索引 Key 行為，沒有替本專案定義上述整組取代規則；因此若使用這種做法，規則、錯誤處理與相容性均屬於套件自己的公開契約，而不是可假設存在的框架行為。

## 三種做法比較

| 面向 | 禁止環境覆蓋 | 原生索引合併 | 自訂整組取代 |
| --- | --- | --- | --- |
| 心智模型 | 單一來源 | 按索引 Key 覆蓋 | 較高來源取代整組 |
| 環境可使用不同清單 | 否 | 可以，但受索引影響 | 可以 |
| 套件實作成本 | 低；若強制來源限制則提高 | 最低 | 最高 |
| 主要風險 | 無法表達環境差異或約定未被遵守 | 尾端殘留與順序依賴 | 自訂語意與跨版本維護成本 |
| 導入重點 | 宣告位置與來源限制 | 展示最終合併結果 | 完整規格與測試矩陣 |

## 診斷最終結果

遇到陣列結果不符合預期時，先查看 Configuration 合併後的完整 Key，而不是只查看 JSON 檔案：

```csharp
var requiredKeys = builder.Configuration
    .GetSection("CentralConfig:CentralRequiredKeys");

foreach (var item in requiredKeys.GetChildren())
{
    Console.WriteLine($"{item.Path} = {item.Value}");
}
```

若還需要確認值由哪一個 Provider 提供，可將 Configuration 轉成 `IConfigurationRoot` 後檢查 `Providers`。診斷輸出可能包含敏感設定值；正式環境應限制輸出範圍，避免直接列印整份 Configuration。

## 適用範圍與限制

- 本文主要依據 .NET 8 的 ASP.NET Core Configuration 文件，適用於標準 `Microsoft.Extensions.Configuration` Provider 模型。
- 若應用程式自行清除、重新排列或新增 Provider，實際優先順序以程式建立的 Pipeline 為準。
- 本文不將上述行為外推到 .NET Framework 4.8 的 `System.Configuration`、`web.config` 或其他未採用相同 Provider 模型的設定機制。
- Configuration Binder 會使用數字 Key 綁定陣列；若索引有缺口，綁定後的 CLR 陣列會被壓成連續索引，不能用 CLR 陣列位置反推原始 Configuration Key。[Microsoft Learn 的 Bind an array 章節](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/?view=aspnetcore-8.0#bind-an-array)有相應範例。
- 本文以版本化的 Microsoft Learn 文件作為行為依據；dotnet/runtime 的 `main` 分支連結用來補充目前實作機制，未來可能變動。

## 第一手資料

- [Configuration in ASP.NET Core：階層資料與多來源陣列](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/?view=aspnetcore-8.0#how-hierarchical-configuration-data-is-organized)
- [Configuration providers in .NET：JSON Provider 與覆蓋順序](https://learn.microsoft.com/en-us/dotnet/core/extensions/configuration-providers#json-configuration-provider)
- [Configuration in ASP.NET Core：Bind an array](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/?view=aspnetcore-8.0#bind-an-array)
- [dotnet/runtime：`JsonConfigurationFileParser`](https://github.com/dotnet/runtime/blob/main/src/libraries/Microsoft.Extensions.Configuration.Json/src/JsonConfigurationFileParser.cs#L52-L64)
- [dotnet/runtime：`ConfigurationRoot`](https://github.com/dotnet/runtime/blob/main/src/libraries/Microsoft.Extensions.Configuration/src/ConfigurationRoot.cs#L102-L114)
- [dotnet/runtime：`InternalConfigurationRootExtensions`](https://github.com/dotnet/runtime/blob/main/src/libraries/Microsoft.Extensions.Configuration/src/InternalConfigurationRootExtensions.cs#L19-L27)
