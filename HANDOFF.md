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
| Central Config Host | Management 的 Set／Remove／current／history 與中央環境設定鍵列表、Distribution 的 Resolve、SQLite 持久化、`/health/ready` 與啟動識別值，以及記錄完整例外的伺服器端 Error 日誌（以 `traceId` 對應 Problem Details）；`/swagger` 提供 Swagger UI。 |
| Client | 啟動設定與設定鍵選擇驗證、HTTP 分發、有限重試與逾時、啟動診斷（最終失敗帶出中央的 `CentralErrorCode` 與 `CentralTraceId`），以及不重新載入的啟動快照設定提供者。 |
| Demo Consumer Host | 在啟動時載入中央設定；提供 `Demo` 區段的有效設定，以及明確標示為不安全的根設定診斷端點。 |
| 測試 | 單元、站內整合／HTTP、套件契約、確定性重試／取消測試，以及使用真正 Central／Demo 程序與 packaged Client 的 E2E 情境 A–J。 |
| 文件與演示 | README、設計與決策文件、短入門導讀，以及 17 段請求的 `.http` 演示與操作說明；HTTP 演示可用 `scripts/start-http-demo.ps1` 一個指令啟動三個程序。 |

目前跨程序 E2E 使用一個 Central 與一個 Demo。HTTP 演示使用一個 Central 與兩個 Demo，展示兩個使用端共用同一份中央值，以及必要設定鍵缺值時的啟動失敗；它以 `ProjectReference` 連結 Client，由人員逐段呼叫，並在標示的步驟以啟動腳本的指令重啟 Demo，不取代使用真正 NuGet 套件的 E2E。原本的完整示範手冊已移除（見下方 #16）。

### 最近完成的工作

示範前審查（[PR #1](https://github.com/Igouist/Demo.DotnetCore.ConfigService/pull/1)，合併提交 `5a9e6c5`）完成下列修正：

- 啟動快照設定提供者接受 `IConfiguration[key] = value` 寫入，使用端在 `AddCentralConfigAsync` 之後呼叫 `WebHost.UseUrls`／`UseSetting` 不再啟動失敗。
- Demo 的 `failed` 事件列出缺少的中央必要設定鍵，並帶出中央的錯誤代碼與 `traceId`。
- Central 的 Error 日誌記錄完整例外；伺服器端日誌界線見決策紀錄 POC-052 的 2026-10-21 修訂。
- `global.json` 接受任一 SDK 10.0.x；SDK 8 的編譯器與格式檢查不相容。
- HTTP 演示擴充為兩個 Demo 與 17 段請求。
- 修正兩個在負載下不穩定的測試：Client 測試的執行緒池飢餓、E2E 取消回呼的競態。

### 目前驗證結果

- CI：[`.github/workflows/ci.yml`](.github/workflows/ci.yml) 在 `push` 到 `master` 與 Pull Request 時，於 `ubuntu-latest` 與 `windows-latest` 執行 README「完整驗證」；最新結果以 [Actions](https://github.com/Igouist/Demo.DotnetCore.ConfigService/actions/workflows/ci.yml) 為準。以下為 CI 之前的手動驗證紀錄。
- 環境：Linux、SDK 10.0.113、.NET 8 ASP.NET Core 執行階段；對應提交 `3c2893b`（已含於 `5a9e6c5`）。
- README「完整驗證」全部通過：format 無變更、Release build 0 warning；CentralHost.Tests 185、Client.Tests 187、DemoHost.Tests 12、EndToEnd.Tests 130。
- HTTP 演示的 17 段請求已以等效 bash 指令在 Linux 上逐段驗證。
- [#2](https://github.com/Igouist/Demo.DotnetCore.ConfigService/issues/2) 第一段（2026-10-21，對應提交 `e61066f`）：在 Linux 以 PowerShell 7.6.7 執行操作說明的全部 PowerShell 指令。指令以互動方式逐段貼入三個 tmux 終端機，使用真正的 `Ctrl+C` 停止與重啟；`.http` 的 01–17 以 httpyac 6.16.7 逐段送出（`httpyac send --line <行號>`）。每段的狀態碼與關鍵欄位均符合預期；第 15 步 Demo A 輸出 `"missingRequiredKeys":["Demo:CentralRequiredMessage"]` 後以非零代碼結束（Linux 為 134）。操作說明第 5 節補上移除暫存目錄的 PowerShell 指令。
- 尚未在 Windows 上執行，也尚未以 Windows PowerShell 5.1 驗證。

### PR #10（已合併，合併提交 `78d8ef7`）

- [#7](https://github.com/Igouist/Demo.DotnetCore.ConfigService/issues/7)：新增[用外部 SQLite 工具查看 Central 資料庫](docs/knowledge/sqlite-external-tool-collation.md)，HTTP 操作說明加上連結。文中的查詢已在 Linux 以 SQLite 3.45.1 與 Python 3 對 Central 建立的資料庫實測。
- [#5](https://github.com/Igouist/Demo.DotnetCore.ConfigService/issues/5)：Client 的重試 Warning 加上 `CentralErrorCode`、`CentralTraceId`，Demo 的 `retry` 標記加上 `centralErrorCode`、`centralTraceId`；值取自觸發該次重試的回應；設計文件 §9.3、Demo／E2E 標記規格、示範手冊標記表與 Client README 已同步。
- 驗證（Linux、SDK 10.0.113）：README「完整驗證」全部通過，format 無變更、Release build 0 warning；CentralHost.Tests 185、Client.Tests 189、DemoHost.Tests 12、EndToEnd.Tests 130。另以 issue #5 的情境實跑（把 `ConfigurationEntryVersions` 改名使 Central 回 `500 persistence_failure`）：Demo 的 3 筆 `retry` 與 1 筆 `failed` 各帶不同的 `centralTraceId`，每一筆都在 Central 日誌找到對應記錄。

### PR #14（已合併，合併提交 `fcbdf99`）

- [#4](https://github.com/Igouist/Demo.DotnetCore.ConfigService/issues/4)：新增 `GET /api/v1/management/configurations?environment=...`，列出中央環境每個設定鍵的顯示拼寫、最新版本、目前狀態與時間；不回傳值、包含已移除的鍵、不分頁，空環境回傳空陣列。契約見設計文件 §4.5（原 §4.5、§4.6 改為 §4.6、§4.7），取捨見決策紀錄 POC-058。`.http` 在 17 之後加一段附錄請求，01–17 的編號與預期結果不變。
- 驗證（Linux、SDK 10.0.113）：README「完整驗證」全部通過，format 無變更、Release build 0 warning；CentralHost.Tests 192、Client.Tests 189、DemoHost.Tests 12、EndToEnd.Tests 130。另以真正的 Central 程序實測列表、空環境與缺少 `environment` 的 `400`。

### PR #15（已合併，合併提交 `d75b78d`）

- [#11](https://github.com/Igouist/Demo.DotnetCore.ConfigService/issues/11)：新增 [`scripts/start-http-demo.ps1`](scripts/start-http-demo.ps1)，一個指令建置並啟動 Central 與兩個 Demo；使用者完成 `.http` 02 後按 Enter 才啟動 Demo，之後輸入 `A`／`B`／`R` 重啟 Demo，`Q` 或 `Ctrl+C` 停止全部並刪除暫存目錄（`-KeepData` 保留）。腳本不寫入設定值，01–17 的預期結果不變。方案比較見決策紀錄 POC-059；[HTTP 操作說明](docs/demo/http-demo-guide.md)改寫為腳本流程，手動流程移到附錄；README 同步。`.editorconfig` 規定 `.ps1` 存成含 BOM 的 UTF-8，供 Windows PowerShell 5.1 正確讀取中文。
- 驗證（Linux、PowerShell 7.6、SDK 10.0.113）：以 tmux 執行腳本，`.http` 01–17 與附錄以 httpyac 逐段送出，狀態碼與關鍵欄位均符合預期；第 15 步輸入 `A` 後腳本顯示 `"missingRequiredKeys":["Demo:CentralRequiredMessage"]` 與結束代碼 134。另測 `Q` 與 `Ctrl+C`（在 Enter 提示與指令選單）都會停止三個程序並刪除暫存目錄、連接埠已被使用與 `Demo__` 環境變數時拒絕啟動、暫存路徑含空白。尚未在 Windows 或 Windows PowerShell 5.1 上執行。

### PR #17（已合併，合併提交 `9645236`）

- [#12](https://github.com/Igouist/Demo.DotnetCore.ConfigService/issues/12)：新增 GitHub Actions workflow，以 Linux／Windows 矩陣執行 README「完整驗證」的五個命令，測試失敗時上傳 TRX；README 加上 CI 徽章與說明。EndToEnd.Tests 先留在同一個 job，待實測時間與穩定性後再決定是否拆開。
- 本機驗證（Linux、SDK 10.0.113）：五個命令全部通過；CentralHost.Tests 192、Client.Tests 189、DemoHost.Tests 12、EndToEnd.Tests 130。`dotnet test` 共 78 秒，其中 EndToEnd.Tests 58 秒。
- CI 首次執行（PR #17，提交 `2aa424a`）兩個平台都通過：`ubuntu-latest` 整個 job 約 2 分鐘，EndToEnd.Tests 45 秒；`windows-latest` 約 3.5 分鐘，EndToEnd.Tests 63 秒。時間可接受，EndToEnd.Tests 不拆 job。

### PR #18（已合併，合併提交 `6489e00`）

- [#16](https://github.com/Igouist/Demo.DotnetCore.ConfigService/issues/16)：移除完整示範手冊，理由見決策紀錄 POC-060。它獨有的兩項內容移到新位置：設定來源優先序的人工展示成為 [HTTP 操作說明](docs/demo/http-demo-guide.md)第 5 節（選用，另啟動 Demo C 於 `5183`，`.http` 新增附錄 B；原附錄改名附錄 A），使用端引用套件的方式寫進 [Client 使用說明](src/Demo.DotnetCore.ConfigService.Client/README.md)的「引用套件」。README、短入門導讀與 Demo／E2E 規格第 10 節同步修訂。
- 驗證（Linux、PowerShell 7.6、SDK 10.0.113）：以啟動腳本執行 Central，照第 5 節的指令在另一個終端機啟動 Demo C，有效設定依序為 `environment-value`、`command-value`，中央值不變，移除環境變數後 `Test-Path` 為 `False`。照「引用套件」的步驟在儲存庫外封裝並建立一個 `net8.0` 使用端，還原、建置並連到 Central，取得中央值。README「完整驗證」全部通過：format 無變更、Release build 0 warning；CentralHost.Tests 192、Client.Tests 189、DemoHost.Tests 12、EndToEnd.Tests 130。

### 進行中的變更（分支 `claude/project-thread-0pr4qk`，尚未合併）

- [#13](https://github.com/Igouist/Demo.DotnetCore.ConfigService/issues/13)：Central 以 Swashbuckle 10.3.0（`SwaggerGen`、`SwaggerUI`）提供 OpenAPI 文件 `/swagger/v1/swagger.json` 與 Swagger UI `/swagger`，一律開啟、不列入 `/health/ready`，取捨見決策紀錄 POC-061。控制器不使用模型繫結，所以 Set、Remove、Resolve 以 `JsonRequestBody` 屬性標示請求型別與範例（中央環境 `UAT`），由 `JsonRequestBodyOperationFilter` 寫入文件；回應以 `ProducesResponseType` 標示成功與 `application/problem+json`。既有端點行為不變。README、HTTP 操作說明第 4 節與設計文件第 4 節開頭已提及網址。
- 驗證（Linux、SDK 10.0.113）：README「完整驗證」全部通過，format 無變更、Release build 0 warning；CentralHost.Tests 200（新增 `OpenApiDocumentTests` 8 個）、Client.Tests 189、DemoHost.Tests 12、EndToEnd.Tests 130。另以真正的 Central 程序（`127.0.0.1:5180`）與 Playwright 操作 Swagger UI，依序對 Set、current、history、列表、Resolve、Remove 按「Try it out」與「Execute」，回應均符合設計文件第 4 節。

### 下一步

1. [#2](https://github.com/Igouist/Demo.DotnetCore.ConfigService/issues/2) 第二段與 [#11](https://github.com/Igouist/Demo.DotnetCore.ConfigService/issues/11) 的 Windows 驗證：維護者在 Windows 上以 `scripts/start-http-demo.ps1` 走一次 HTTP 演示，分別用 Windows PowerShell 5.1 與 PowerShell 7；留意執行原則、系統保留的連接埠範圍、示範當天使用的 `.http` 工具，以及結束後連接埠是否釋放。
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
| 在使用端引用 NuGet 套件 | [Client 使用說明](src/Demo.DotnetCore.ConfigService.Client/README.md)的「引用套件」；套件模式的完整驗證由 E2E 負責。 |

從專案根目錄，依 README 的「完整驗證」執行 restore、format、Release build 與 tests；SDK 版本以 [global.json](global.json) 為準。修改行為時，先選最小且穩定的測試邊界；跨程序整合行為再由 E2E 補強。

HTTP 演示需在寫入必要值後才啟動 Demo，並在標示的步驟暫停重啟。它不會自動判定測試通過；啟動腳本結束時會停止它啟動的程序並刪除暫存資料，手動流程則要自行停止與清理。

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
