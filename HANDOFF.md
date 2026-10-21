# 開發交接：Config Distribution POC

更新日期：2026-10-21。

本文件供維護者或 Agent 接手公開版本。專案用於概念驗證、團隊需求討論與教學展示，不以正式部署為目標。下一階段的功能與驗收條件尚未確認。

## 1. 接手第一步

1. 確認工作目錄包含 `Demo.DotnetCore.ConfigService.sln`，並檢查當前分支與未提交變更。若已遷移至獨立 Repo，以新 Repo 為開發依據。
2. 閱讀 [README](README.md) 掌握架構、啟動與測試方式；不熟悉 .NET 設定機制時，先讀[短入門導讀](docs/getting-started.md)。
3. 依下方文件入口核對要修改的契約，確認變更範圍與驗收條件，再開始實作。

公開版本的遷移與基線驗證流程見第 5 節；功能擴充須另行確認需求。

## 2. 已完成範圍

初始 .NET 8 POC 的三個元件、測試與演示文件已具備：

| 項目 | 現況 |
| --- | --- |
| Central Config Host | Management 的 Set／Remove／current／history 與中央環境設定鍵列表、Distribution 的 Resolve、SQLite 持久化、`/health/ready` 與啟動識別值，以及記錄完整例外的伺服器端 Error 日誌（以 `traceId` 對應 Problem Details）。 |
| Client | 啟動設定與設定鍵選擇驗證、HTTP 分發、有限重試與逾時、啟動診斷（最終失敗帶出中央的 `CentralErrorCode` 與 `CentralTraceId`），以及不重新載入的啟動快照設定提供者。 |
| Demo Consumer Host | 在啟動時載入中央設定；提供 `Demo` 區段的有效設定，以及明確標示為不安全的根設定診斷端點。 |
| 測試 | 單元、站內整合／HTTP、套件契約、確定性重試／取消測試，以及使用真正 Central／Demo 程序與 packaged Client 的 E2E 情境 A–J。 |
| 文件與演示 | README、設計與決策文件、短入門導讀、完整示範手冊，以及 17 段請求的 `.http` 演示與操作說明。 |

目前跨程序 E2E 使用一個 Central 與一個 Demo。HTTP 演示使用一個 Central 與兩個 Demo，展示兩個使用端共用同一份中央值，以及必要設定鍵缺值時的啟動失敗；它以 `ProjectReference` 連結 Client，並由人員逐段呼叫、手動重啟，不取代使用真正 NuGet 套件的 E2E 或完整示範手冊。

### 最近完成的工作

示範前審查（[PR #1](https://github.com/Igouist/Demo.DotnetCore.ConfigService/pull/1)，合併提交 `5a9e6c5`）完成下列修正：

- 啟動快照設定提供者接受 `IConfiguration[key] = value` 寫入，使用端在 `AddCentralConfigAsync` 之後呼叫 `WebHost.UseUrls`／`UseSetting` 不再啟動失敗。
- Demo 的 `failed` 事件列出缺少的中央必要設定鍵，並帶出中央的錯誤代碼與 `traceId`。
- Central 的 Error 日誌記錄完整例外；伺服器端日誌界線見決策紀錄 POC-052 的 2026-10-21 修訂。
- `global.json` 接受任一 SDK 10.0.x；SDK 8 的編譯器與格式檢查不相容。
- HTTP 演示擴充為兩個 Demo 與 17 段請求。
- 修正兩個在負載下不穩定的測試：Client 測試的執行緒池飢餓、E2E 取消回呼的競態。

### 目前驗證結果

- 環境：Linux、SDK 10.0.113、.NET 8 ASP.NET Core 執行階段；對應提交 `3c2893b`（已含於 `5a9e6c5`）。
- README「完整驗證」全部通過：format 無變更、Release build 0 warning；CentralHost.Tests 185、Client.Tests 187、DemoHost.Tests 12、EndToEnd.Tests 130。
- HTTP 演示的 17 段請求已以等效 bash 指令在 Linux 上逐段驗證。
- [#2](https://github.com/Igouist/Demo.DotnetCore.ConfigService/issues/2) 第一段（2026-10-21，對應提交 `e61066f`）：在 Linux 以 PowerShell 7.6.7 執行操作說明的全部 PowerShell 指令。指令以互動方式逐段貼入三個 tmux 終端機，使用真正的 `Ctrl+C` 停止與重啟；`.http` 的 01–17 以 httpyac 6.16.7 逐段送出（`httpyac send --line <行號>`）。每段的狀態碼與關鍵欄位均符合預期；第 15 步 Demo A 輸出 `"missingRequiredKeys":["Demo:CentralRequiredMessage"]` 後以非零代碼結束（Linux 為 134）。操作說明第 5 節補上移除暫存目錄的 PowerShell 指令。
- 尚未在 Windows 上執行，也尚未以 Windows PowerShell 5.1 驗證。

### PR #10（已合併，合併提交 `78d8ef7`）

- [#7](https://github.com/Igouist/Demo.DotnetCore.ConfigService/issues/7)：新增[用外部 SQLite 工具查看 Central 資料庫](docs/knowledge/sqlite-external-tool-collation.md)，HTTP 操作說明加上連結。文中的查詢已在 Linux 以 SQLite 3.45.1 與 Python 3 對 Central 建立的資料庫實測。
- [#5](https://github.com/Igouist/Demo.DotnetCore.ConfigService/issues/5)：Client 的重試 Warning 加上 `CentralErrorCode`、`CentralTraceId`，Demo 的 `retry` 標記加上 `centralErrorCode`、`centralTraceId`；值取自觸發該次重試的回應；設計文件 §9.3、Demo／E2E 標記規格、示範手冊標記表與 Client README 已同步。
- 驗證（Linux、SDK 10.0.113）：README「完整驗證」全部通過，format 無變更、Release build 0 warning；CentralHost.Tests 185、Client.Tests 189、DemoHost.Tests 12、EndToEnd.Tests 130。另以 issue #5 的情境實跑（把 `ConfigurationEntryVersions` 改名使 Central 回 `500 persistence_failure`）：Demo 的 3 筆 `retry` 與 1 筆 `failed` 各帶不同的 `centralTraceId`，每一筆都在 Central 日誌找到對應記錄。

### 進行中的變更（分支 `claude/project-thread-6drc34`，尚未合併）

- [#4](https://github.com/Igouist/Demo.DotnetCore.ConfigService/issues/4)：新增 `GET /api/v1/management/configurations?environment=...`，列出中央環境每個設定鍵的顯示拼寫、最新版本、目前狀態與時間；不回傳值、包含已移除的鍵、不分頁，空環境回傳空陣列。契約見設計文件 §4.5（原 §4.5、§4.6 改為 §4.6、§4.7），取捨見決策紀錄 POC-058。`.http` 在 17 之後加一段附錄請求，01–17 的編號與預期結果不變。
- 驗證（Linux、SDK 10.0.113）：README「完整驗證」全部通過，format 無變更、Release build 0 warning；CentralHost.Tests 192、Client.Tests 189、DemoHost.Tests 12、EndToEnd.Tests 130。另以真正的 Central 程序實測列表、空環境與缺少 `environment` 的 `400`。

### 下一步

1. [#2](https://github.com/Igouist/Demo.DotnetCore.ConfigService/issues/2) 第二段：維護者在 Windows 上實際走一次 HTTP 演示，留意 PowerShell 版本、系統保留的連接埠範圍、示範當天使用的 `.http` 工具，以及 `Ctrl+C` 後連接埠是否釋放。
2. [#3](https://github.com/Igouist/Demo.DotnetCore.ConfigService/issues/3)：在示範會議上取得架構決策共識，結論寫回決策紀錄，再依結論開立後續 Issue。

其他待辦以 [GitHub Issues](https://github.com/Igouist/Demo.DotnetCore.ConfigService/issues) 為準，本文件不重複列出。

## 3. 延續已接受的設計

- 同一個中央環境與設定鍵共用一份中央值，識別不分大小寫；使用端自行宣告需要哪些設定鍵，中央不登錄使用端。
- Client 在建立主機前完成載入，建立不可變啟動快照。中央 Set／Remove 不會更新執行中的使用端；重新啟動才取得新快照。
- 必要設定鍵缺值會阻止啟動。允許後備的設定鍵，只有在合法中央回應確認缺值時，才交由本機設定來源決定有效值。Client 只對符合條件的失敗有限重試，重試成功可繼續啟動；最終載入失敗則阻止啟動。傳輸、HTTP、逾時或無效回應不能當成本機後備的理由。
- 設定來源優先序由低到高為：基底 JSON、主機環境 JSON、中央快照、User Secrets、環境變數、命令列。中央環境與 .NET 主機環境是不同概念。
- Set、Remove、再次 Set 都附加新的 Entry Version；歷史不覆寫。空字串、從未建立與已移除是不同狀態。

詳細契約以 [CONTEXT](CONTEXT.md) 的術語與[已接受設計](docs/design/config-distribution-poc.md)為準。調整既有行為時，同步更新受影響的測試與契約文件。

POC 僅在隔離環境使用非敏感資料。根設定診斷端點會公開未遮蔽的有效設定；一般演示使用範圍限定端點即可。執行階段更新、離線快取、認證授權、多設定鍵原子發佈及正式環境維運，均不屬於目前範圍；這些限制不是待補齊的正式部署工作清單。

## 4. 文件入口與驗證

| 要處理的工作 | 先讀 |
| --- | --- |
| 認識架構與執行方式 | [README](README.md)、[短入門導讀](docs/getting-started.md)。 |
| 調整術語、行為或 HTTP 契約 | [CONTEXT](CONTEXT.md)、[POC 設計](docs/design/config-distribution-poc.md)。 |
| 理解設計理由 | [決策紀錄](docs/decisions/poc-decision-log.md)、[共享值 ADR](docs/adr/0001-share-value-by-environment-and-key.md)、[設定鍵選擇 ADR](docs/adr/0002-consumer-owned-key-selection.md)。 |
| 修改 Client 或就緒探針 | [Client 使用說明](src/Demo.DotnetCore.ConfigService.Client/README.md)、[Central readiness 設計](docs/design/central-readiness.md)。 |
| 修改 Demo 或跨程序測試 | [Demo／E2E 設計](docs/design/demo-host-e2e.md)。 |
| 向團隊逐步演示 | [HTTP 操作說明](docs/demo/http-demo-guide.md)、[HTTP 呼叫檔](docs/demo/config-distribution-demo.http)。 |
| 驗證 NuGet 封裝與完整演示 | [完整示範手冊](docs/demo/config-distribution-poc-runbook.md)。 |

從專案根目錄，依 README 的「完整驗證」執行 restore、format、Release build 與 tests；SDK 版本以 [global.json](global.json) 為準。修改行為時，先選最小且穩定的測試邊界；跨程序整合行為再由 E2E 補強。

HTTP 演示需在寫入必要值後才啟動 Demo，並在標示的步驟暫停重啟。它不會自動判定測試通過或清理程序；完整示範手冊則會在結束時停止自己啟動的程序。

文件敘述與程式現況的核對不能代替實際驗證。驗證紀錄應列出執行命令、結果與對應的提交；遷移後須重新建立基線，不能以舊環境的通過紀錄代替。

## 5. 公開版本遷移與基線驗證

需要將公開副本遷移至獨立 Repo 時，依下列流程處理；各項完成狀態以實際提交與驗證紀錄為準：

1. 將公開版本的原始碼、測試、文件與建置設定複製到獨立目錄，保留 `.gitignore` 等隱藏設定。省略舊 `.git`、`bin`、`obj`、測試輸出、套件、日誌及暫存資料庫。
2. 確認新 Repo 的目標位址。如果 Client 的 `RepositoryUrl` 需要變更，同步調整 [Client 專案](src/Demo.DotnetCore.ConfigService.Client/Demo.DotnetCore.ConfigService.Client.csproj)與[套件契約測試](tests/Demo.DotnetCore.ConfigService.Client.Tests/Packaging/PackageContractTests.cs)的預期值。
3. 在新目錄初始化 Git，以公開版本建立第一筆提交，不帶入舊 Repo 的歷史。執行完整驗證，並重做需要展示的 HTTP 流程。
4. 確認來源與文件無誤後，再依維護者授權 Push。確認新 Repo 保存完整後，才移除遷移來源的副本。
5. 記錄遷移結果、對應提交與實際驗證結果。若要擴充功能，先確認範圍及驗收條件。

完成條件：新 Repo 保留完整原始碼、測試、文件與建置設定，驗證結果已記錄，且不需舊工作目錄或私人文件即可理解與執行本專案。

目前未包含多個 Demo 同時連到同一中央服務的 E2E；逐步 HTTP 演示以人工方式使用一個 Central 與兩個 Demo。多個 Demo 的 E2E 可另行評估，尚非既定擴充範圍，也不是目前 POC 的未完成缺陷。

## 6. 後續維護交接文件

完成一段工作後，更新已完成內容、目前驗證結果與下一個明確步驟。提交識別只引用新 Repo 可查閱的歷史；文件連結使用相對路徑。保留當前狀態即可，避免累積對話紀錄、私人背景或過期的待辦事項。
