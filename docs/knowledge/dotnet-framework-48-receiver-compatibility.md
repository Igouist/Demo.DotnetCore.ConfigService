# .NET Framework 4.8 Receiver 相容性知識筆記

本文整理 Config Distribution 未來支援 .NET Framework 4.8 Consumer Application 時，需要先理解的設定模型、Host 生命週期、套件相依性與驗證工作。本文是知識筆記，不是決策紀錄，也不替專案選定 Receiver 介面或套件形式。

目前先以 .NET 8 完成示範，不表示 POC 排除 `net48`；`net48` Receiver **可能在 .NET 8 demonstration 之後成為第二階段**。第二階段能否成立，應由代表性舊系統盤點與可執行 spike 的結果判斷，而不是只看某個 NuGet 套件顯示 `net48` compatible。

## 結論先行

- 中央端的 Environment、Configuration Key、Key Selection、Missing Behavior、批次 Resolve HTTP 契約與啟動時固定快照等語意，大多不依賴 Host framework，可望由共用 core 重用。
- .NET 8 Adapter 把中央值放入可排序的 `Microsoft.Extensions.Configuration` Provider pipeline；傳統 .NET Framework 程式則常由 `System.Configuration.ConfigurationManager` 讀取 `appSettings`、`connectionStrings` 或具型別的 custom section。兩者不是同一個擴充點。
- 讓既有 `ConfigurationManager` 呼叫「看起來不用改」的做法，以 .NET Framework Configuration Builders 最接近；但 Builder API 是同步的 section-load hook，對遠端 HTTP、錯誤呈現、啟動 Logger 與各 section 的映射都有額外設計成本。
- 新的中央設定 accessor 邊界最清楚、依賴最小，但需要修改呼叫端；在 `net48` 直接採用 `Microsoft.Extensions.Configuration` 能保留 Provider 心智模型，卻不會自動讓既有 `ConfigurationManager` 呼叫讀到同一份資料。
- 套件標示可用於 `net48` 只證明 NuGet 能選出相容 asset。舊專案的 `packages.config`／`PackageReference`、transitive dependencies、實際 deploy output 與 binding redirects 仍要逐一驗證。

## 兩套設定模型的差異

| 面向 | .NET 8 `Microsoft.Extensions.Configuration` | .NET Framework `System.Configuration` |
| --- | --- | --- |
| 基本模型 | 由多個 Provider 組成 `IConfiguration`；來源可明確加入與排序 | 由 `machine.config`、應用程式／網站設定檔及 section handler 組成 Framework configuration system |
| 取值介面 | 階層式字串 Key，例如 `A:B:C`；也可綁定 options | `ConfigurationManager.AppSettings`、`ConnectionStrings`、`GetSection`，custom section 通常回傳特定型別 |
| 覆蓋機制 | 同一完整 Key 由較晚加入的 Provider 覆蓋；實際次序由 builder 決定 | 依設定檔階層、`add`／`remove`／`clear` 與 section 自身規則合併，並非任意可插入的通用 Provider 清單 |
| 擴充點 | `IConfigurationSource` + `ConfigurationProvider` | `ConfigurationSection`／`ConfigurationElement`，或 .NET Framework 4.7.1+ 的 `System.Configuration.ConfigurationBuilder` |
| 讀取與快取 | `IConfigurationRoot` 持有 Provider；是否 reload 由 Provider 能力與設定決定 | `ConfigurationManager` 的 runtime 讀取是唯讀、使用單一快取執行個體，並具多執行緒感知 |

`.NET` 官方文件說明 Provider 可來自 JSON、XML、INI、環境變數等來源，且後加入的 Provider 對相同 Key 有較高優先權；自訂來源則實作 `IConfigurationSource` 與 `ConfigurationProvider`。[Configuration providers in .NET](https://learn.microsoft.com/en-us/dotnet/core/extensions/configuration-providers) 與 [Implement a custom configuration provider](https://learn.microsoft.com/en-us/dotnet/core/extensions/custom-configuration-provider) 分別說明組合與擴充方式。

相對地，`System.Configuration.ConfigurationManager` 的 `AppSettings`、`ConnectionStrings` 與 `GetSection` 是對目前應用程式預設 Configuration 的 section API；官方也明確說明這些 runtime 成員是唯讀、使用快取，而編輯檔案要先以 `OpenExeConfiguration` 等 API 取得 `Configuration` 物件。[ConfigurationManager class](https://learn.microsoft.com/en-us/dotnet/api/system.configuration.configurationmanager?view=netframework-4.8.1) 因此，不能假設在應用程式已開始讀取後，把中央字典寫進記憶體就會像新增一個 .NET 8 Provider 一樣透明地改變所有 `ConfigurationManager` 結果。

## 可整合的 Framework 設定表面

### `appSettings`

既有程式最常以 `ConfigurationManager.AppSettings["key"]` 取得平面字串；這個 API 與 `App.config` 的標準用法見 [.NET Framework：Read application settings](https://learn.microsoft.com/en-us/dotnet/framework/configure-apps/read-app-settings)。如果代表性系統主要只用這個表面，Framework-native Adapter 的相容範圍相對容易界定。

Configuration Builders 對 `<appSettings>` 有特別支援：`Strict` 模式只替換檔案中已存在的 Key，`Greedy` 模式把外部來源的所有 Key 加入結果。[Configuration builders for ASP.NET](https://learn.microsoft.com/en-us/aspnet/config-builder#keyvalue-configuration-builders-settings) 這兩種模式可分別對應「中央只覆蓋已宣告 placeholder」與「中央結果加入 section」的候選策略，但中央 Required／Fallback 語意、缺值與 precedence 仍須由本專案明確定義，不能只沿用 Builder 模式名稱。

### `connectionStrings`

`connectionStrings` 不是單純的任意字串字典；每一項同時有 `name`、`connectionString` 與 `providerName`，且可能先繼承 `machine.config` 的內容，再由 `<clear/>`、`<remove/>`、`<add/>` 改變集合。[ADO.NET：Connection strings and configuration files](https://learn.microsoft.com/en-us/dotnet/framework/data/adonet/connection-strings-and-configuration-files) 說明了這個結構與繼承行為。

Configuration Builders 也特別處理 `<connectionStrings>`，但 Receiver 仍需先回答：中央 Configuration Value 是否只替換 `connectionString`、`providerName` 是否永遠留在本機、缺值是否保留本機 entry，以及中央 Removal 是否等同「未提供中央覆蓋」而不是刪除本機 entry。這些都是 Config Distribution 的映射語意，不是 Framework API 已替專案回答的問題。

### Custom sections

Custom section 可由 `ConfigurationSection`、`ConfigurationElement` 與 collection 建成具型別物件，呼叫端再由 `ConfigurationManager.GetSection` 取得並轉型；官方 API 文件同時指出 `GetSection` 只讀 runtime 資訊。[ConfigurationManager.GetSection](https://learn.microsoft.com/en-us/dotnet/api/system.configuration.configurationmanager.getsection?view=netframework-4.8.1) 因此，一份平面的中央 `Dictionary<string,string>` 無法自然保證重建每個舊系統 custom section 的 XML shape、型別轉換、validator 與 collection 規則。

Configuration Builder 基底可在 section 載入時以 `ProcessConfigurationSection` 處理 section object，或以 `ProcessRawXml` 處理原始 XML；`Expand` 模式在 XML 解析前替換 token，且不只限於 `appSettings` 與 `connectionStrings`。[System.Configuration.ConfigurationBuilder API](https://learn.microsoft.com/en-us/dotnet/api/system.configuration.configurationbuilder?view=netframework-4.8.1) 與 [Configuration builders：Mode](https://learn.microsoft.com/en-us/aspnet/config-builder#mode) 支持這些擴充能力。它們證明 custom section 可以有整合點，但不保證一個通用 Key mapping 能相容所有既有 section；代表性 section 必須實測。

### Microsoft Configuration Builders 的定位

Configuration Builders 自 .NET Framework 4.7.1 起可用，目的是讓標準 Framework configuration system 在載入時從外部來源建構或修改設定；Microsoft 的 `KeyValueConfigBuilder` 基底可供自訂外部 Key-Value source 實作 `GetValue`／`GetAllValues`。[Configuration builders for ASP.NET](https://learn.microsoft.com/en-us/aspnet/config-builder) 與 [`Microsoft.Configuration.ConfigurationBuilders.Base` NuGet](https://www.nuget.org/packages/Microsoft.Configuration.ConfigurationBuilders.Base/2.0.0) 顯示 `net471` asset 可供 `net48` 使用。

重要限制是 `System.Configuration.ConfigurationBuilder` 暴露的處理方法都是同步 API。若 Builder 在首次 section load 時呼叫中央 HTTP，Receiver 就必須在同步 hook 內完成具逾時上限的工作，並處理例外如何被 Framework configuration system 包裝及呈現。這條路應以 spike 驗證，不能只把現有 async .NET 8 registration 包一層 `.Result`；Microsoft 的 async 指引指出，在舊 ASP.NET request thread 等具有 `SynchronizationContext` 的環境同步阻塞 Task 可能造成 deadlock。[Common async/await bugs：Deadlocks from blocking](https://learn.microsoft.com/en-us/dotnet/standard/asynchronous-programming-patterns/common-async-bugs#deadlocks-from-blocking-on-async-code)

## 不同 Host 的啟動邊界

Receiver 的網路與解析 core 應保持 Task-based async；各 Host Adapter 再選擇最早且可控制失敗的啟動邊界。目標是「在第一個依賴中央設定的工作開始前完成一次載入」，而不是在每個 request、job 或 timer callback 中重新 Resolve。

### ASP.NET Framework 與 OWIN/Katana

- IIS-hosted ASP.NET 的設定檔通常是 `web.config`；ASP.NET 專屬設定管理可用 `WebConfigurationManager`，而 Windows 應用程式使用 `ConfigurationManager`。[ADO.NET configuration files](https://learn.microsoft.com/en-us/dotnet/framework/data/adonet/connection-strings-and-configuration-files) 也提醒設定檔名稱與位置取決於 Host。
- Katana 的 `Startup.Configuration(IAppBuilder)` 負責組裝 OWIN pipeline；IIS/System.Web 與 self-host 的 Host 行為不同，但可以共用 Startup 類別與 middleware pipeline。[An overview of Project Katana](https://learn.microsoft.com/en-us/aspnet/aspnet/overview/owin-and-katana/an-overview-of-project-katana) 說明 Host、server 與 application pipeline 的分工。
- 這個 `Configuration` 入口本身是同步 `void` 形狀。因此 spike 要確認中央載入放在 Configuration Builder、`Application_Start`、OWIN Startup 之前的 bootstrap，還是 OWIN pipeline 建立時的受控同步邊界；同時觀察 IIS recycle、warm-up、同一 AppDomain 內的重入與啟動失敗外觀。

### .NET Framework Windows Service

`ServiceBase.OnStart` 是 Service Control Manager 啟動 service 時呼叫的初始化入口；Service 可在 Running 狀態持續工作。[ServiceBase.OnStart](https://learn.microsoft.com/en-us/dotnet/api/system.serviceprocess.servicebase.onstart?view=netframework-4.8.1) 與 [Introduction to Windows Service applications](https://learn.microsoft.com/en-us/dotnet/framework/windows-services/introduction-to-windows-service-applications#service-lifetime) 描述這個生命週期。

設計上應先取得中央快照，再啟動 worker thread／timer；若載入失敗，應讓 service 明確啟動失敗，而不是留下「SCM 顯示 Running、實際工作未啟動」的半初始化狀態。因 `OnStart` 不是 Task-returning API，Adapter 需要一個受控同步邊界；其等待上限、[`RequestAdditionalTime`](https://learn.microsoft.com/en-us/dotnet/api/system.serviceprocess.servicebase.requestadditionaltime?view=netframework-4.8.1) 是否需要、停止期間的 CancellationToken 來源與 Event Log／既有 Logger 整合，都要在 service spike 中量測與定義。

### Console、排程與一次性程式

C# 的 entry point 可回傳 `Task` 或 `Task<int>`；使用 `await` 時可宣告 `async Main`，而 `Task<int>` 可把失敗轉成 process exit code。[C# `Main` 與命令列參數](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/program-structure/main-command-line) 因此使用 C# 7.1+ compiler 的 `net48` console app，可以在 `Main` 直接 await Receiver，再進入真正工作，是三種 Host 中最自然的 async 邊界。

若舊專案 compiler 或入口形狀不能使用 async Main，應把唯一的同步等待限制在 process entry point，並讓共用 library code 維持 async、避免 `async void`。是否允許升級 compiler、如何保留 exit code、排程器如何判斷失敗，應列入該應用程式盤點。

## 三種 Receiver 候選路線

| 候選 | 核心作法 | 優點 | 主要代價與風險 | 最適合驗證的對象 |
| --- | --- | --- | --- | --- |
| 透明 legacy `ConfigurationManager` 整合 | 以 Configuration Builder 或 section-specific Adapter，讓既有 `AppSettings`／`ConnectionStrings`／`GetSection` 呼叫看到中央結果 | 呼叫端改動最少；保留既有 Framework config 消費方式 | 同步載入邊界；section mapping 複雜；precedence 分散在設定檔階層與 Builder mode；早期啟動 Logger 較難注入 | 大量直接呼叫 `ConfigurationManager`、短期難改 call site 的系統 |
| 新的中央 accessor | 啟動時 `LoadAsync` 取得 snapshot，經 package-owned `Get`／`TryGet` 或 injected interface 讀取 | framework-neutral；HTTP、取消、錯誤與測試 seam 清楚；不干擾舊設定快取 | 所有中央化 Key 的 call site 必須遷移；本機 fallback 與 type conversion 要自行定義；可能同時存在兩套設定入口 | 可漸進改造、想明確區隔中央與本機設定的系統 |
| 在 `net48` 採用 `Microsoft.Extensions.Configuration` | 在 Framework 程式建立 `ConfigurationBuilder`，加入本機來源與中央 Provider，再以 `IConfiguration` 消費 | 與 .NET 8 相同的 Provider／完整 Key 心智模型；Provider precedence 可顯式排列 | 不會自動橋接既有 `ConfigurationManager`；增加 `Microsoft.Extensions.*` 相依圖、JSON／file provider 部署與 binding redirect 工作；Host 不一定已有 DI／Generic Host | 已使用 `Microsoft.Extensions.*`，或可接受把設定存取遷移到 `IConfiguration` 的系統 |

第三條路在套件層面是可行候選，而不是空泛假設：[`Microsoft.Extensions.Configuration` 8.0.0](https://www.nuget.org/packages/Microsoft.Extensions.Configuration/8.0.0) 與 [`Microsoft.Extensions.Configuration.Json` 8.0.0](https://www.nuget.org/packages/Microsoft.Extensions.Configuration.Json/8.0.0) 都包含 `net462` asset，NuGet 因而把 `net48` 列為 compatible。這裡引用 `8.0.0` 只為證明該版本的 TFM 與依賴形狀，**不是版本建議**；真正 spike 應選定仍受支援且已套安全修補的版本，鎖定完整 dependency graph 後測試。

三條路也可以分層組合，例如共用 async loader + `net48` Configuration Builder、共用 async loader + accessor；不應為了表面統一而讓 HTTP core 直接相依 `System.Configuration` 或 `IHostApplicationBuilder`。

## 套件與 Target Framework 策略

### 分開的 `net48` Adapter 套件

一個可能的包裝方式是：

- framework-neutral core：Domain validation、Resolve DTO、HTTP client、response validation、retry／timeout orchestration；
- .NET 8 Host Adapter：`IConfiguration` Provider placement 與 `IHostApplicationBuilder` registration；
- .NET Framework 4.8 Adapter：`System.Configuration`／Configuration Builder／legacy Host startup integration。

優勢是 consumer 只承擔自己 Host 所需的依賴，而且 Framework-specific public API 不必用條件編譯塞進同一 assembly。代價是套件數、版本協調與 contract tests 增加；若 core 直接 target `netstandard2.0`，仍需確認其所有直接與間接依賴都能在兩端工作。

### Multi-target 同一套件或 shared core

.NET Standard 2.0 可由 .NET Framework 4.6.1+ 與 modern .NET 實作；官方 library guidance 建議需要 Framework breadth 時考慮 `netstandard2.0`，需要 framework-specific API 時用 multi-targeting，讓 NuGet 選擇最匹配的 asset。[Cross-platform targeting](https://learn.microsoft.com/en-us/dotnet/standard/library-guidance/cross-platform-targeting) 也特別指出：即使程式碼相同，只要有 package dependencies，增加 Framework-specific target 可能改善 .NET Framework consumer 的 binding redirect 體驗。

因此可比較兩種結構：

1. core 僅 target `netstandard2.0`，Host Adapters 分別 target `net8.0`／`net48`；
2. Receiver 套件以 `<TargetFrameworks>net8.0;net48</TargetFrameworks>` multi-target，共享大部分 source，並在 framework-specific target 提供不同 Adapter。

SDK-style project 可用 `TargetFrameworks` 與條件式 item／編譯建立多 TFM package，NuGet 會從同一套件選取適合 consumer 的 assembly。[NuGet：Support multiple .NET frameworks in your project file](https://learn.microsoft.com/en-us/nuget/create-packages/multiple-target-frameworks-project-file) 不過 TFM 能 build 並不等於 runtime 已通過；兩個 target 都要各自跑單元、契約、pack-install 與 Host smoke tests。

### 依賴圖、`packages.config` 與 binding redirects

- 舊式 .NET Framework project 可能用 `packages.config`，也可能用 `PackageReference`。兩者互斥；Framework project 支援 `PackageReference`，但傳統 ASP.NET 對它只有有限支援。[PackageReference in project files](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files#project-type-support) 與 [`packages.config` reference](https://learn.microsoft.com/en-us/nuget/reference/packages-config) 說明這個差異。
- `packages.config` 通常把 `lib/<tfm>` 下所有 assemblies 加為 reference，不使用為 `PackageReference` 設計的 `ref` assets；若要同時支援兩種 consumer，pack output 與 nuspec 的 reference/dependency groups 要實際檢查。[Select assemblies referenced by projects](https://learn.microsoft.com/en-us/nuget/create-packages/select-assemblies-referenced-by-projects#packagesconfig-support) 說明這項 asset-selection 差異。
- `Microsoft.Extensions.Configuration.Json` 的 `net462` asset 會帶入 Configuration、FileProviders 與 `System.Text.Json` 等 dependencies；[`Microsoft.Extensions.Configuration.Json` 8.0.0 dependency list](https://www.nuget.org/packages/Microsoft.Extensions.Configuration.Json/8.0.0#dependencies-body-tab) 可見這不是單一 DLL。是否真的需要 JSON／file providers，應由採用路線決定，避免把不需要的整套依賴帶入 legacy app。
- .NET Framework 以 assembly binding redirects 統一不同版本的相依 assembly。桌面應用程式 target 4.5.1+ 時可自動產生 redirect；Web app 因必須修改來源 `web.config`，Visual Studio 會在衝突時提示加入，而不是自動修改。[Automatic binding redirection](https://learn.microsoft.com/en-us/dotnet/framework/configure-apps/how-to-enable-and-disable-automatic-binding-redirection) 說明兩者差異。官方 NuGet 相容性指引也要求 Framework app 能處理 NuGet assemblies 的 binding redirects。[NuGet package compatibility rules](https://learn.microsoft.com/en-us/dotnet/standard/library-guidance/nuget-package-compatibility-rules#assembly-versioning-on-net-framework)

每個代表性 app 都應以它原本的 package management 形式做 install／restore／build／deploy smoke test；不能只在新的 SDK-style `net48` 測試專案成功後，就推論舊 ASP.NET `packages.config` project 也相容。

## 可以共用與必須另決定的界線

### 原則上可不變的 domain／HTTP 語意

只要共用 core 避免使用 `net8.0`-only API，下列內容不需要因 Host 改成 .NET Framework 而改變：

- Environment 與 Configuration Key 的 canonical、case-insensitive identity；
- Configuration Key 結構與 reserved `CentralConfig` root 驗證；
- Consumer-owned Key Selection，以及 Central-Required／Fallback-Allowed 的 duplicate、overlap 與 missing rules；
- 空字串與 Missing／Removal 的區別；
- 一次批次 Resolve，request 不攜帶 Consumer identity，response 回傳 values 與 missing keys；
- Distribution route、HTTP method、JSON contract、Problem Details status／code mapping 與安全的 response validation；
- 啟動時載入一次並形成固定 snapshot；
- 逾時、bounded retry、caller cancellation、中央失敗時 fail-fast、value-safe diagnostics 等行為契約；
- 中央 Environment 與 Host Environment 是不同概念，中央 Environment 必須由本機 bootstrap 明確選擇。

HTTP 實作可共用並不代表可每次建立新的 `HttpClient`。Microsoft 的 [.NET HttpClient guidelines](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines#recommended-use) 對 .NET Framework 建議用 `IHttpClientFactory` 管理 instances，並警告逐 request 建立 client 可能耗盡可用連接埠。未採 Generic Host／DI 的舊 app 是否接收 caller-owned `HttpClient`、使用 factory、或由 Adapter 管理長生命週期 client，仍要配合套件依賴與 ownership 決定。

### 後續必須決定或量測的 Adapter 細節

- **Precedence**：中央值相對於 `machine.config`、base `web.config`／`app.config`、Web.config transforms、環境變數、命令列與 custom sections 的位置；`Strict`、`Greedy`、token expansion 或 accessor fallback 不能混稱為同一種 precedence。
- **Bootstrap 來源**：`Enabled`、Base Address、中央 Environment 與 Key Selection 放在哪個本機 section；怎麼避免中央值覆蓋 bootstrap 自己。
- **Host Environment**：ASP.NET Framework／OWIN、Windows Service 與 console 沒有共同的 `IHostEnvironment.EnvironmentName` 慣例；中央 Environment 雖仍應明確，但其本機 source、部署替換方式與命名對照需個別設計。
- **Logging**：舊 app 可能使用 `Trace`、Event Log、log4net、NLog 或其他框架。雖然 [`Microsoft.Extensions.Logging.Abstractions` 8.0.0](https://www.nuget.org/packages/Microsoft.Extensions.Logging.Abstractions/8.0.0) 有 `net462` asset，仍需決定是保留 caller-supplied `ILogger`、提供中立 callback／diagnostic event，或寫 Host-specific bridge；Receiver 不應暗中建立第二套 Logger pipeline。
- **同步／非同步**：哪些 Host 能 await、哪些只有同步 hook；受控同步邊界的位置、deadlock 測試、timeout 與 cancellation 如何投影為 Host startup failure。
- **生命週期與 reload**：本專案的 startup snapshot 是否在 IIS AppDomain recycle、service restart 與 console 每次執行都得到一致效果；不應因 Configuration Builder 或 JSON Provider 支援 reload 就暗中改成 runtime refresh。
- **部署**：x86／x64、GAC 中舊 assembly、copy-local、IIS bin deployment、binding redirects、TLS／proxy 與 service account 權限。

## 代表性舊系統盤點清單

先選 3–5 個真正代表 deployment reality 的 app。若實際組合允許，至少涵蓋以下四種；若 OWIN 是部署重點，可把 IIS-hosted 與 self-host 分成兩個樣本：

| 樣本 | 建議代表性 | 主要觀察點 |
| --- | --- | --- |
| A | IIS-hosted ASP.NET Framework MVC／Web API／Web Forms | `web.config` hierarchy／transform、`Application_Start`、OWIN 是否存在、AppDomain recycle、Web binding redirects |
| B | OWIN/Katana self-host | `Startup.Configuration`、實際 process host、pipeline 建立前的可用 bootstrap、同步邊界 |
| C | `ServiceBase` Windows Service | `OnStart`／`OnStop`、SCM startup timeout、service account、Event Log／既有 Logger、worker 啟動順序 |
| D | Console／排程／批次程式 | compiler language version、async Main、exit code、排程器 timeout、工作目錄與 `.exe.config` 位置 |
| E（選配） | `packages.config` 最舊或 custom sections 最複雜的 app | install scripts、dependency conflicts、typed section 與第三方元件直接讀取 ConfigurationManager |

每個 app 建立同一份 inventory：

- 精確 target framework、Visual Studio／MSBuild／NuGet 版本、SDK-style 與否、C# language version、AnyCPU／x86／x64；
- `packages.config` 或 `PackageReference`、直接與 transitive package 清單、現有 binding redirects、GAC／copy-local 特例；
- Host 類型、process identity、工作目錄、設定檔實際路徑、部署與 transform／token replacement 流程；
- `ConfigurationManager.AppSettings`、`ConnectionStrings`、`WebConfigurationManager`、`GetSection` 與第三方 framework 讀取的 call-site 數量；
- 每個 custom section 的 type、XML shape、validator、collection 與 `<configSource>`／`file` 等外部分檔用法；
- 擬中央化 Key 數、Key spelling、值大小、空字串／缺值案例、連線字串的 `providerName` ownership；
- 最早讀取每個 Key 的時間點，以及 OWIN／DI container／timer／worker／static constructor 的初始化順序；
- 是否能 await startup、可接受啟動延遲、Host 如何顯示失敗、取消與 shutdown signal 從哪裡來；
- 現有 HTTP stack、proxy／TLS、service account 網路權限、`HttpClient` ownership 與 DNS／連線管理方式；
- 現有 logging abstraction、啟動初期可用的 sink、敏感值遮蔽規則、trace／correlation 能力；
- 可用的 unit／integration／deployment test harness，以及可接受的 call-site 改造範圍。

## 建議後續 spike 問題

每個 spike 都應產出可執行測試、pack output／dependency graph 與 Host 啟動紀錄，而不只是 sample code。

1. 在一個真實 `packages.config` ASP.NET Framework app 與一個 `PackageReference` app 安裝候選 package，restore／build／deploy 後有哪些新增 DLL、redirect 與衝突？
2. Configuration Builder 首次讀取 `appSettings` 時執行一次遠端 Resolve，是否能在 IIS recycle、平行首個 request 與中央 timeout 下保持單次載入、無 deadlock、可診斷且明確 fail-fast？
3. `Strict`、`Greedy` 與 token expansion 各自如何表達 Central-Required、Fallback-Allowed、空字串與 Missing；結果是否與 .NET 8 demonstration 的有效值語意一致？
4. `connectionStrings` 的中央 Value 與本機 `providerName` 如何組合；`machine.config` 繼承、`<clear/>` 與中央 Missing 對最終集合有何影響？
5. 選兩個真實 custom sections，能否用通用 mapping 處理，還是每個 section 都需要 Adapter；第三方 library 若在應用程式 bootstrap 之前讀取 section 會發生什麼事？
6. OWIN／ASP.NET、Windows Service 與 console 各自最早可安全完成 `LoadAsync` 的位置在哪裡；唯一同步邊界能否在 timeout／cancellation／exception 下可靠結束？
7. 採 `Microsoft.Extensions.Configuration` 時，能否只引入最小 packages 並明確建立本機與中央 Provider 順序；既有 `ConfigurationManager` call sites 的遷移量是多少？
8. 分開 Adapter package 與 multi-target package 各自的 consumer install 體驗、public API、binary size、dependency graph、binding redirects 與測試矩陣差多少？
9. 共用 core 在 `net8.0` 與 `net48` 是否對大小寫、URI normalization、JSON、timeout、retry、cancellation、Problem Details 與 malformed response 通過同一組 contract tests？
10. 啟動 Logger 應使用 `ILogger`、中立 callback 還是 Host bridge，才能在三類 Host 最早的載入階段可用，同時不建立第二個 container、不中斷主控制流程、也不輸出 Configuration Value？

## 適用範圍與限制

- 本文只說明可驗證的 Framework／NuGet 行為與候選整合面，不聲稱任一 Receiver 路線已通過實際舊系統驗證。
- NuGet 的 `compatible` 標示是以 TFM 與 package assets 推導；它不涵蓋 consumer 的所有 transitive dependency、GAC、binding、Host lifecycle 或 deployment 差異。
- Configuration Builders 文件主要以 ASP.NET Framework 為例。若用於 Windows Service 或 console，仍要以該 Host 的 `App.config`／`.exe.config` 與啟動路徑實測。
- .NET 8 Provider precedence 不應直接外推為 .NET Framework 的 section precedence；兩端若要交付相同「有效值」，必須先定義映射，再以跨 TFM contract tests 證明。
- 這份筆記保留多條候選路線；專案選擇與狀態應留在專門的決策文件，而不是在此文宣告。

## 第一手資料

- [Configuration providers in .NET](https://learn.microsoft.com/en-us/dotnet/core/extensions/configuration-providers)
- [Implement a custom configuration provider](https://learn.microsoft.com/en-us/dotnet/core/extensions/custom-configuration-provider)
- [System.Configuration.ConfigurationManager](https://learn.microsoft.com/en-us/dotnet/api/system.configuration.configurationmanager?view=netframework-4.8.1)
- [.NET Framework：Read application settings](https://learn.microsoft.com/en-us/dotnet/framework/configure-apps/read-app-settings)
- [ADO.NET：Connection strings and configuration files](https://learn.microsoft.com/en-us/dotnet/framework/data/adonet/connection-strings-and-configuration-files)
- [Configuration builders for ASP.NET](https://learn.microsoft.com/en-us/aspnet/config-builder)
- [System.Configuration.ConfigurationBuilder](https://learn.microsoft.com/en-us/dotnet/api/system.configuration.configurationbuilder?view=netframework-4.8.1)
- [An overview of Project Katana](https://learn.microsoft.com/en-us/aspnet/aspnet/overview/owin-and-katana/an-overview-of-project-katana)
- [.NET Framework Windows Service lifetime](https://learn.microsoft.com/en-us/dotnet/framework/windows-services/introduction-to-windows-service-applications#service-lifetime)
- [`ServiceBase.RequestAdditionalTime`](https://learn.microsoft.com/en-us/dotnet/api/system.serviceprocess.servicebase.requestadditionaltime?view=netframework-4.8.1)
- [C# `Main` 與命令列參數](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/program-structure/main-command-line)
- [Common async/await bugs](https://learn.microsoft.com/en-us/dotnet/standard/asynchronous-programming-patterns/common-async-bugs)
- [Cross-platform targeting for .NET libraries](https://learn.microsoft.com/en-us/dotnet/standard/library-guidance/cross-platform-targeting)
- [NuGet PackageReference](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files)
- [NuGet `packages.config` reference](https://learn.microsoft.com/en-us/nuget/reference/packages-config)
- [.NET Framework automatic binding redirection](https://learn.microsoft.com/en-us/dotnet/framework/configure-apps/how-to-enable-and-disable-automatic-binding-redirection)
- [NuGet package compatibility rules](https://learn.microsoft.com/en-us/dotnet/standard/library-guidance/nuget-package-compatibility-rules)
- [.NET HttpClient guidelines](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines)
- [`Microsoft.Extensions.Configuration` 8.0.0 NuGet](https://www.nuget.org/packages/Microsoft.Extensions.Configuration/8.0.0)
- [`Microsoft.Extensions.Configuration.Json` 8.0.0 NuGet](https://www.nuget.org/packages/Microsoft.Extensions.Configuration.Json/8.0.0)
- [`Microsoft.Configuration.ConfigurationBuilders.Base` 2.0.0 NuGet](https://www.nuget.org/packages/Microsoft.Configuration.ConfigurationBuilders.Base/2.0.0)
