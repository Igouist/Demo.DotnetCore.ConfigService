# 示範使用端主機與跨程序 E2E 設計規格

> **審查狀態：** 已核准（Approved）2026-09-05
>
> **目標：** 初始 .NET 8 POC 的第三階段
>
> **修訂 — 2026-09-11：** 已接受的[中央就緒狀態與啟動識別值設計](central-readiness.md)取代中央設定主機原本以固定時間推斷穩定性的方式。此修訂保留已完成的 E2E 設計，僅調整其中已被取代的就緒狀態細節。

本規格細化已接受的[設定分發 POC 設計](config-distribution-poc.md)，供第三個實作階段使用。標準術語仍以 [`CONTEXT.md`](../../CONTEXT.md) 為準，已接受的產品行為也仍是權威依據。本文件定義示範使用端主機、套件使用、跨程序測試及操作手冊的具體機制；整合設計僅在階段層級描述這些內容。

## 1. 目的與成功條件

本階段加入 POC 的最後一項交付成果：使用用戶端套件並公開最終生效 `IConfiguration` 的 .NET 8 示範使用端主機。它也加入自動化的跨程序證據，證明已實作的中央設定主機與用戶端套件，能透過公開的 HTTP 與 NuGet 介面共同運作。

本階段符合下列條件即為成功：

- 一般方案在乾淨取出的工作目錄中，無須既有的本機套件來源，仍可還原及建置；
- 成品模式驗證會封裝目前的用戶端、以該確切套件還原示範主機，並啟動產生的示範組件；
- 示範端點實作已接受的範圍限定與不安全根層級契約，不揭露設定提供者的來源資訊；
- 實際的中央與示範程序通過分配給 E2E 的已接受情境；
- 用戶端模組邊界上的確定性測試，持續保護精確時序與呼叫端取消語意；
- 組件層級測試夾具擁有並清理共用的套件／建置狀態，每個情境則擁有並清理自己的程序、連接埠、SQLite 檔案與其他執行階段狀態；以及
- 工程師可依一份以 PowerShell 為主要操作方式的手冊，安全地重現 POC。

## 2. 範圍與已確定的選擇

本階段包含四項交付成果：

1. `Demo.DotnetCore.ConfigService.DemoHost`：一般 ASP.NET Core Web API。
2. `Demo.DotnetCore.ConfigService.DemoHost.Tests`：聚焦於端點與設定慣例的測試。
3. `Demo.DotnetCore.ConfigService.EndToEnd.Tests`：實際程序的測試框架與情境。
4. `docs/demo/config-distribution-poc-runbook.md`：與實際成品一致的示範程序。

本設計採用兩項實作選擇：

- **混合式用戶端使用方式。** 一般開發使用 `ProjectReference`；成品模式 E2E 使用暫存本機來源中新封裝的用戶端 `PackageReference`，重新建置相同的示範原始碼。
- **情境 J 的分層證據。** 實際程序 E2E 證明重試後恢復及最終啟動失敗。既有的確定性用戶端測試，仍是精確呼叫端權杖取消、單次嘗試逾時、總逾時、重試分類及取消優先順序的權威依據。

第二項選擇只改變驗證責任的歸屬，不會弱化或改變用戶端套件契約。

## 3. 專案與模組配置

本階段從下列配置開始：

```text
Directory.Packages.props

src/
  Demo.DotnetCore.ConfigService.DemoHost/
    Demo.DotnetCore.ConfigService.DemoHost.csproj
    Program.cs
    appsettings.json
    Diagnostics/
      StartupConsoleLogger.cs
    Configuration/
      EffectiveConfigurationReader.cs
    Http/
      DemoConfigurationController.cs
      DemoConfigurationResponses.cs

tests/
  Demo.DotnetCore.ConfigService.DemoHost.Tests/
    Demo.DotnetCore.ConfigService.DemoHost.Tests.csproj
    Configuration/
      EffectiveConfigurationReaderTests.cs
    Http/
      DemoConfigurationHttpTests.cs
    Diagnostics/
      StartupConsoleLoggerTests.cs
    Conventions/
      DemoAppSettingsConventionTests.cs
    Infrastructure/
      DemoHostWebApplicationFactory.cs

  Demo.DotnetCore.ConfigService.EndToEnd.Tests/
    Demo.DotnetCore.ConfigService.EndToEnd.Tests.csproj
    Scenarios/
      ConfigurationDistributionScenarios.cs
      ResilienceScenarios.cs
    Infrastructure/
      PackagedDemoFixture.cs
      PocProcessEnvironment.cs
      ChildProcess.cs
      LoopbackPortAllocator.cs
      TemporaryDirectory.cs
      ManagementClient.cs
      ManagementContracts.cs

docs/
  demo/
    config-distribution-poc-runbook.md
```

名稱仍屬實作細節，找到更清楚的區域邊界時可再調整；但本規格的責任與相依方向已確定。

`EffectiveConfigurationReader` 是具體的深模組（deep module）。其精簡 API 回傳相對於 `Demo:*` 或根層級的值；實作負責列舉、路徑處理、null 篩除、保留空字串及不區分大小寫的設定鍵具現化。控制器直接依賴此具體讀取器；由於應用程式實作只有一個轉接器，不新增 `IEffectiveConfigurationReader` 接縫。

`DemoConfigurationController` 是 HTTP 轉接器。它選取讀取器操作、建立本機回應 DTO，並套用回應標頭。它不包含設定合併、中央載入或來源資訊邏輯。

`StartupConsoleLogger` 是呼叫端擁有的 `ILogger`，只在註冊期間傳給用戶端。它為每個用戶端生命週期事件向標準錯誤寫入一筆以換行分隔的 JSON 記錄。每筆記錄都有 `"source":"demo-central-config"`，並且恰好使用下列其中一組屬性（JSON 屬性順序不屬於契約）：

- `loaded`：`source`、`event`、`valueCount`、`missingFallbackKeyCount`、`attemptCount`、`elapsedMs`；
- `retry`：`source`、`event`、`attemptCount`、`failureKind`、可為 null 的 `httpStatusCode`、`delayMs`；以及
- `failed`：`source`、`event`、`failureKind`、`attemptCount`、可為 null 的 `httpStatusCode`、`elapsedMs`、`missingRequiredKeys`。

`missingRequiredKeys` 只在 `failureKind` 為 `MissingRequiredKeys` 時列出缺少的中央必要設定鍵名稱，其他情況為空陣列；這與用戶端契約允許在最終 Error 日誌公開這些名稱的規則一致，讓示範者不必除錯就能看出缺少哪個設定鍵。記錄器排除中央環境、其他要求的設定鍵、缺少的允許後備設定鍵名稱、設定值、經格式化的架構訊息及例外序列化。這個由示範主機擁有的穩定協定，讓操作手冊與 E2E 同步不依賴架構 Console Logger 的裝飾格式。

E2E 的 `ChildProcess` 模組封裝程序建立、非同步讀取完所有輸出、等待就緒狀態、提早結束診斷、程序樹終止及等待程序結束。`PocProcessEnvironment` 組合此模組，形成先中央、後示範的工作流程。測試方法使用 HTTP 與生命週期操作，不直接操作 `Process`。

## 4. 示範使用端主機的啟動與設定

示範主機嚴格遵循下列啟動順序：

```text
WebApplication.CreateBuilder(args)
  -> create a caller-owned startup console logger
  -> await AddCentralConfigAsync(logger)
  -> register Demo HTTP adapters
  -> Build
  -> map Demo HTTP adapters
  -> RunAsync
```

示範主機在註冊前建立 `StartupConsoleLogger`，直接使用 `Console.Error` 作為輸出，並擁有其生命週期；用戶端不建立也不處置它。記錄器在整個 `RunAsync` 期間持續可用，不擁有架構的記錄提供者，也不需要暫時的服務提供者，或 Web SDK 共用架構之外的額外套件。聚焦測試涵蓋其標記對應及精確的 JSON 屬性允許清單。示範主機不攔截並重新解讀 `CentralConfigLoadException`，也不把啟動失敗轉成仍在執行的 HTTP 應用程式。

納入版本控制的基底 `appsettings.json` 包含兩個設定鍵選擇陣列及不敏感的本機範例。它以明確停用模式作為安全的獨立啟動預設值；操作手冊與 E2E 程序環境會明確啟用中央載入：

```json
{
  "CentralConfig": {
    "Enabled": false,
    "BaseAddress": "http://127.0.0.1:5180/",
    "Environment": "Demo",
    "CentralRequiredKeys": [
      "Demo:CentralRequiredMessage"
    ],
    "FallbackAllowedKeys": [
      "Demo:FallbackAllowedMessage"
    ]
  },
  "Demo": {
    "FallbackAllowedMessage": "local-fallback",
    "LocalOnlyMessage": "local-only"
  }
}
```

預設停用讓聚焦的示範 HTTP 測試與意外的獨立啟動，不依賴中央設定主機是否可用。這不是隱含的啟用狀態：`Enabled` 仍須明確存在且為 Boolean。與實際成品一致的操作手冊，一律展示開啟中央載入所需的明確覆寫。

只有基底 `appsettings.json` 宣告 `CentralRequiredKeys` 與 `FallbackAllowedKeys`。環境專用 JSON、環境變數及命令列引數可覆寫 `Enabled`、`BaseAddress`、`Environment` 及一般的 `Demo:*` 值，但不重新宣告或擴充這兩個陣列。慣例測試會解析納入版本控制的檔案，保護這個受支援的範例；它不宣稱用戶端能對任意使用端強制驗證設定來源。

## 5. 示範 HTTP 介面

### 5.1 建議使用的範圍限定介面

`GET /api/v1/demo/effective-configuration` 回傳：

```json
{
  "scope": "Demo",
  "effectiveValues": {
    "CentralRequiredMessage": "central-value",
    "FallbackAllowedMessage": "local-fallback"
  }
}
```

它包含所有路徑以 `Demo:` 開頭、相對路徑非空且值非 null 的項目，移除該前綴，保留空字串，即使父節點同時有子節點也保留其值，並省略值為 null 的群組節點。直接指派給根設定鍵 `Demo` 本身的值會被排除。沒有符合條件的值時，`effectiveValues` 為空的 JSON 物件。

### 5.2 不安全的根層級介面

`GET /api/v1/demo/unsafe/root-configuration` 回傳相對於根層級的非 null 值，以及下列精確警告：

```json
{
  "scope": "Root",
  "warning": "This response may expose sensitive configuration values.",
  "effectiveValues": {
    "CentralConfig:Enabled": "true",
    "Demo:CentralRequiredMessage": "central-value"
  }
}
```

不安全介面一律會對應至路由。它沒有功能旗標、授權、自動遮蔽或主機環境限制。

兩個介面都會：

- 回傳 `200 OK`，媒體類型為 `application/json`；
- 加入 `Cache-Control: no-store`；
- 只讀取最終合併後的 `IConfiguration`；
- 不在處理要求時向中央設定主機發出要求；
- 不揭露設定提供者的來源資訊、原始設定分發回應或項目版本；
- 保留 .NET Configuration 不區分大小寫的識別語意，不承諾 JSON 屬性順序；以及
- 不呼叫應用程式自行擁有的記錄器來記錄回傳的設定鍵或值。

範圍限定與不安全回應使用分開的記錄型別，讓範圍限定表示中完全沒有警告成員，而不是將它序列化為 `null`。

## 6. 混合式 ProjectReference 與 PackageReference 模式

`Directory.Packages.props` 宣告 `Demo.DotnetCore.ConfigService.Client` 的穩定 `0.1.0` 基準版本，符合儲存庫 NuGet Central Package Management 的要求。只宣告此集中管理版本，不會在示範主機仍使用預設專案參考模式時還原該套件。

示範專案有一個明確的 Boolean 建置屬性 `UsePackagedClient`，預設為 `false`，用來選擇用戶端參考模式：

- 預設模式：以 `ProjectReference` 參考用戶端專案；
- 套件模式：以 `PackageReference` 參考 `Demo.DotnetCore.ConfigService.Client`，並設定 `VersionOverride="$(DemoClientPackageVersion)"`。

兩個參考互斥。集中管理的 `0.1.0` `PackageVersion` 滿足 NuGet Central Package Management 的相符項目要求；接著 `VersionOverride` 選取操作手冊使用的確切成品或 E2E 的唯一成品，無須改寫集中管理檔案。套件模式若缺少 `DemoClientPackageVersion`，還原與建置會以清楚的 MSBuild 錯誤失敗，且此模式要求 Central Package Version Override 支援維持啟用。還原與建置必須傳入完全相同的兩個屬性。它們是建置／測試機制，不是執行階段設定或公開的應用程式選項。

每次執行 E2E 測試組件時，`PackagedDemoFixture` 會：

1. 從測試組件的位置找到儲存庫根目錄；
2. 選擇不易衝突的預發行版本，例如 `0.1.0-e2e.<unique-id>`；
3. 將目前用戶端以 Release 還原及建置至夾具擁有的中繼／輸出路徑，再將該輸出封裝至新的暫存本機套件來源；
4. 驗證新封裝檔中的用戶端 DLL，與該隔離 Release 建置具有相同雜湊值；
5. 使用該確切版本，以套件模式還原示範主機；
6. 隔離主要套件目錄、NuGet HTTP／外掛快取、CLI 主目錄，以及兩個專案的中繼／輸出路徑；
7. 使用夾具產生的 `NuGet.Config`，清除繼承的來源、只宣告暫存套件來源與 `nuget.org`、將確切的 `Demo.DotnetCore.ConfigService.Client` 套件 ID 專屬對應至暫存來源、將其餘 `*` 套件對應至 `nuget.org`，且只允許已還原的方案層級全域套件資料夾，作為固定版本外部套件的唯讀後備來源；以及
8. 只建置一次套件模式的示範主機，供所有 E2E 情境使用。

確切 ID／萬用字元的套件來源對應（Package Source Mapping）消除多來源的 `NU1507` 歧義，並防止用戶端套件轉而從外部來源解析。夾具驗證 `project.assets.json` 與套件模式示範主機的相依性資訊清單，都將所選用戶端版本識別為套件相依性；也驗證解析出的用戶端成品來自夾具擁有的主要套件目錄，且其 DLL 雜湊值與剛封裝的 DLL 相同。還原前，它會確認所選唯一版本不存在於共用後備來源；若衝突則產生另一個識別值。它不讀取使用者層級的 NuGet 來源或認證設定。實際 E2E 程序一律啟動此套件模式的示範輸出。一般方案建置仍使用預設的專案參考模式，因此乾淨取出的工作目錄不需要預先建置的本機套件。

既有的用戶端 `PackageContractTests` 仍負責預設 `0.1.0` 封裝檔項目、相依性中繼資料、README 及 DLL 位元組一致性。唯一的 E2E 預發行版本可證明程序不會在未察覺的情況下重用先前快取的用戶端成品。

## 7. 跨程序測試框架與隔離

測試框架透過目前的 `dotnet` 主機啟動已建置、依賴架構的 DLL，設定 `UseShellExecute=false`、重新導向 stdout／stderr，且不顯示子程序視窗。它不以 `WebApplicationFactory` 取代 E2E 程序。

每個情境擁有：

- 一個唯一的作業系統暫存根目錄；
- 該根目錄下的一個 SQLite 資料庫絕對路徑；
- 一個中央環境名稱；
- 各自獨立的中央與示範回送連接埠；
- 各自獨立的程序記錄與取消／生命週期狀態；以及
- 僅限該情境需要的不敏感設定值。

中央設定主機每次啟動嘗試也擁有一個全新、`N` 格式的小寫 GUID。測試框架只以 `CentralHealth__InstanceId` 將它傳入已清除無關與敏感項目的子程序環境；不將它加入命令列，也不修改測試執行器的環境。

應用程式子程序使用在清理環境前解析出的 `dotnet` 主機絕對路徑：先使用有效的 `DOTNET_HOST_PATH`，其次在目前程序是 muxer 時使用該程序路徑，最後才透過一次性的 `PATH` 解析作為後備。清理後的應用程式環境將 `HOME`／`USERPROFILE`、`DOTNET_CLI_HOME`、`TEMP`／`TMP`／`TMPDIR` 及應用程式專屬設定指向情境根目錄；只保留必要的執行階段位置變數，例如 `DOTNET_ROOT`，以及適用時的 Windows `SystemRoot`／`WINDIR` 變數組；不保留任意的 `CentralConfig__`、`Demo__`、連線字串、Proxy、認證或秘密變數。建置／還原程序使用另一個由夾具擁有的環境，額外提供隔離的 NuGet 快取路徑，並只提供已宣告套件來源所需的 Proxy／憑證變數。兩種方式都不盲目繼承測試執行器的完整環境。設定覆寫透過各個 `ProcessStartInfo.Environment` 指派；測試絕不修改程序全域的環境變數。

連接埠的選取方式是繫結回送連接埠零、記錄分配到的連接埠、在啟動前立即關閉保留，並在位址已占用失敗時重試整個啟動。中央與示範主機透過標準命令列選項 `--urls http://127.0.0.1:<port>` 接收選定的繫結；情境專屬設定使用 `ProcessStartInfo.ArgumentList` 或已清理的環境項目，不使用 Shell 命令字串。不共用固定測試連接埠或儲存庫資料庫。

中央設定主機先啟動。就緒狀態輪詢呼叫 `GET /health/ready`，只接受精確的 `200 application/json` 回應、僅有一個值的 `Cache-Control: no-store`，以及恰好包含 `status: ready` 與此次全新且相符的啟動識別值的內容。接著，以該情境有效且從未建立的中央環境及 `Demo:Readiness` 設定鍵，呼叫既有管理介面的目前值路由，並要求既有的 `404 application/problem+json` 回應，錯誤碼為 `configuration_not_found`。第一個操作證明位址的程序所有權；第二個操作證明選定的管理路由與資料庫操作已完成。就緒狀態操作、管理介面證據及最後的自有程序結束觀察，共用原本的 15 秒期限，且各自都與框架擁有的程序結束訊號競速。測試準備階段接著透過管理 HTTP 寫入初始值。示範主機的就緒狀態輪詢呼叫建議使用的示範介面，只有取得預期的 `200` 回應才成功。

不同但有效的就緒識別值，可直接判定外部接聽程序占用位址。另一種所有權觀察，是在仍存活的自有程序輸出中觀察到位址已占用標記。格式錯誤或其他無效的就緒狀態回應仍屬契約失敗，絕不轉成位址重試。兩種分類都保留既有、具時限的子程序終止與清理責任。

從程序啟動到結束，stdout 與 stderr 都會並行讀取至完成。等待標記會觀察兩個串流、有自己的期限，且在程序提早結束時立即失敗。就緒狀態失敗也會區分程序提早結束，與程序仍存活但已超過就緒期限。失敗診斷會指出程序、結束代碼、生命週期階段及擷取的安全記錄。測試框架絕不將完整的不安全根層級回應加入失敗診斷。

情境清理先阻止新的 HTTP 工作，若框架擁有的程序樹仍在執行則終止它，等待完全結束，處置 HTTP 用戶端與輸出讀取器，最後才移除其 SQLite、WAL/SHM、記錄及執行階段目錄。所有情境都釋放不可變輸出後，組件夾具清理才移除共用套件來源、主要套件／快取、CLI 主目錄、中繼及建置目錄。清理失敗會保持可見，不會被悄悄忽略。

所有 E2E 情境都屬於同一個不平行執行的 xUnit 集合。共用的是不可變的套件建置輸出，不共用執行階段狀態。

## 8. 驗證責任與情境

| 情境 | 實際程序證據 | 較低層級的權威證據 |
| --- | --- | --- |
| A. 中央必要值 | 設定必要值、啟動示範主機，並透過範圍限定介面觀察該值 | 既有的中央與用戶端契約測試 |
| B. 啟動快照 | 示範主機執行時變更中央設定、觀察舊值、重新啟動示範主機，再觀察新值 | 用戶端的不可變設定提供者測試 |
| C. 允許後備 | 以兩個獨立案例驗證：從未建立與已移除的後備設定鍵，在啟動後仍讓基底 JSON 的值生效 | 用戶端回應／缺值行為測試 |
| D. 必要設定缺失 | 以兩個獨立案例驗證：從未建立與已移除的必要設定鍵，會阻止示範主機就緒並產生安全的最終失敗診斷 | 公開的具型別例外測試 |
| E. 空字串 | 設定中央空字串值，並透過範圍限定介面觀察 `""` | 中央／用戶端 UTF-8 與回應測試 |
| F. 歷程與還原 | Set、Remove、Set；驗證依序排列的版本 1–3、目前狀態，以及示範主機啟動後的還原值 | 中央儲存庫與 HTTP 測試 |
| G. 設定提供者優先順序 | 依序以僅中央設定、環境變數覆寫、命令列覆寫重新啟動 | 用戶端插入位置測試 |
| H. 示範範圍 | 驗證範圍限定介面的排除、不安全介面的納入／警告、父子節點同時有值、媒體類型及 `no-store` | 聚焦的示範 HTTP 測試 |
| I. 大小寫識別語意 | 在管理介面、中央環境、設定鍵選擇及示範存取中使用不同大小寫 | 中央持久化與用戶端成員資格測試 |
| J. 韌性／控制流程 | 至少一次真正的傳輸重試後恢復；另行觀察具時限的最終程序失敗 | 確定性用戶端測試負責精確的重試類別、時序、單次嘗試／總逾時、呼叫端權杖識別及取消優先順序 |

重試恢復情境先透過中央設定主機將初始值寫入暫存資料庫，停止中央設定主機，讓示範主機對不可用的位址啟動，等待示範記錄器發出穩定且不洩漏設定值的第 1 次嘗試 `retry` 標記，再於下一次嘗試前，使用相同連接埠與資料庫重新啟動中央設定主機。示範主機最終必須發出嘗試次數大於 1 的 `loaded` 標記、進入就緒狀態，並公開已寫入的初始值。E2E 不解析預設 .NET Console Logger 的裝飾格式，也不依賴任意等待時間來推斷是否發生重試。

最終失敗情境讓中央設定主機持續不可用。示範主機絕不得就緒，必須發出安全的 `failed` 標記，並在啟動後 45 秒內以非零代碼結束；此時限包含套件的 30 秒額度及程序／排程餘裕。測試不重複每種可重試 HTTP 狀態，也不斷言精確的實際經過時間；確定性用戶端測試已負責該矩陣及精確時間額度。

大小寫識別情境透過管理介面設定 `UAT` / `demo:centralrequiredmessage`，將示範主機的中央環境設定為 `uat`，在基底檔案選取 `Demo:CentralRequiredMessage`，透過不帶參數的範圍限定示範端點觀察該值，並以第三種大小寫查詢相同的管理設定項目。端點沒有設定鍵引數；它證明不同大小寫的選擇與設定分發回應已進入最終設定，不是另外執行識別查詢。

刻意不以作業系統主控台訊號或示範專用控制設定，人工製造呼叫端取消。子程序結束無法證明原始權杖的識別，而且平台專屬的主控台訊號會增加測試不穩定性，卻無法取代對用戶端的直接斷言。

## 9. 聚焦的示範測試

聚焦的示範測試以納入版本控制的明確停用模式執行，再於已知優先順序位置加入受控的記憶體內設定來源。涵蓋項目如下：

- `Demo:*` 下的相對路徑；
- 排除非 `Demo` 設定鍵；
- 保留空字串；
- 省略值為 null 的群組節點；
- 父節點與子節點同時有值；
- 生效值的優先順序，而非設定提供者的來源資訊；
- 精確的範圍限定與不安全回應形狀；
- 精確的不安全警告；
- `application/json` 與 `Cache-Control: no-store`；
- 空的生效值物件；
- 不存在來源資訊／版本成員；
- 無須不安全啟用設定，兩個路由就都會完成對應；
- 兩個設定鍵選擇陣列在實體檔案中都由基底檔案擁有；以及
- 穩定且不洩漏設定值的啟動標記對應與欄位允許清單。

這些測試不建立假的用戶端傳輸，也不重測用戶端的重試／載入內部機制。它們透過讀取器 API 與示範 HTTP 介面測試。

## 10. 示範操作手冊

`docs/demo/config-distribution-poc-runbook.md` 以 PowerShell 為主要操作方式，且與實際成品一致。主要操作流程如下：

1. 驗證固定版本的 SDK，並以 Release 還原／建置；
2. 將用戶端 `0.1.0` 封裝至已忽略的 `artifacts/demo/feed`；
3. 建立已忽略、本次示範專用的 NuGet 設定／快取／輸出根目錄，再還原及建置示範主機，且兩個命令都傳入相同的 `UsePackagedClient=true` 與 `DemoClientPackageVersion=0.1.0`；
4. 使用 SQLite 絕對路徑，在範例 URL `http://127.0.0.1:5180` 啟動中央設定主機；
5. 透過管理 HTTP 建立不敏感的必要／後備／歷程值；
6. 明確啟用中央載入，在範例 URL `http://127.0.0.1:5181` 啟動套件模式的示範主機；
7. 展示範圍限定值、後備、空字串、大小寫識別語意、優先順序、歷程／還原及啟動快照行為；
8. 示範主機持續執行時變更中央設定，再重新啟動示範主機以顯示新快照；
9. 只將不安全根層級呼叫列為選用步驟，並加上醒目警告；以及
10. 先停止兩個程序，再移除資料庫、WAL/SHM、套件來源、快取與輸出。

操作手冊採用與 E2E 相同的確切 ID／萬用字元套件來源對應，並使用各自獨立的套件、中繼及輸出絕對路徑，讓套件模式無法重用預設專案參考的資產。它說明如何替換已被占用的範例連接埠，並透過 `--urls` 傳入兩個繫結。在可行時使用具名的 PowerShell 變數，避免修改整個環境；絕不使用真實連線字串或認證資料，也不宣稱不安全介面適用於隔離示範之外的用途。

不新增獨立的一鍵協調指令碼。自動化生命週期的所有權屬於 E2E 測試框架；操作手冊仍提供每個操作人員步驟的可讀證據。

## 11. 錯誤處理與診斷

用戶端的啟動載入、HTTP、回應、必要設定缺失、逾時與傳輸失敗，保留已實作的 `CentralConfigLoadException` 契約。由於應用程式尚未啟動，示範主機不將它們轉成 HTTP 回應。呼叫端取消在用戶端介面仍維持為帶有原始權杖的 `OperationCanceledException`。

示範端點讀取沒有預期的領域失敗。非預期例外使用一般 ASP.NET Core 失敗行為；本階段不為示範主機新增第二套 Problem Details 契約。

E2E 基礎設施例外描述測試框架操作與安全的程序狀態，不描述產品值。清理例外會附加回報，且不掩蓋主要情境失敗。子程序輸出與 HTTP 斷言訊息，不得回顯不安全根層級負載或繼承的秘密。

## 12. 驗證關卡

本階段完成關卡如下：

```powershell
dotnet restore Demo.DotnetCore.ConfigService.sln -maxcpucount:1 -nodeReuse:false
dotnet format Demo.DotnetCore.ConfigService.sln --verify-no-changes --no-restore
dotnet build Demo.DotnetCore.ConfigService.sln -c Release --no-restore -maxcpucount:1 -nodeReuse:false -p:EnforceCodeStyleInBuild=true
dotnet test Demo.DotnetCore.ConfigService.sln -c Release --no-build --no-restore -maxcpucount:1 -nodeReuse:false
git diff --check
```

完整方案測試包含既有中央／用戶端測試套件、聚焦的示範測試、預設套件契約測試，以及套件用戶端的跨程序 E2E。聚焦專案命令可在 RED/GREEN 循環中執行較小範圍，但無法取代此關卡。

聚焦回歸測試驗證就緒狀態標頭必須精確且只有單一值，並驗證共用期限到期後，啟動逾時不會等待尚未完成的輸出分類。最終所有權關卡的回歸測試，也證明它暫停後續執行之前，管理介面的防護檢查已成功完成。

## 13. 明確排除的目標

本階段不新增：

- 存活狀態或啟動路由、正式環境監控、重新啟動政策或測試專用路由；
- 示範主機的延後取消、失敗注入或 E2E 控制設定；
- 共用的 HTTP DTO 組件；
- 固定的 E2E 連接埠或共用的儲存庫 SQLite 檔案；
- 執行階段自動更新、重新載入、輪詢、快取或最後已知良好狀態行為；
- 身分驗證、授權、遮蔽、認證資料或 TLS 政策；
- 替代的正式環境持久化提供者或抽象；
- 正式環境監控、部署、備份或復原政策；
- .NET Framework 4.8 接收端（Receiver）；或
- 獨立的示範協調可執程序式或指令碼。

無須變更 `CONTEXT.md` 或任一份已接受的 ADR：此設計不引入新的領域術語，也不新增難以逆轉的領域決策。
