# 開發交接：Config Distribution POC

更新日期：2026-10-14。

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
| Central Config Host | Management 的 Set／Remove／current／history、Distribution 的 Resolve、SQLite 持久化，以及 `/health/ready` 與啟動識別值。 |
| Client | 啟動設定與設定鍵選擇驗證、HTTP 分發、有限重試與逾時、啟動診斷，以及不可變的設定提供者。 |
| Demo Consumer Host | 在啟動時載入中央設定；提供 `Demo` 區段的有效設定，以及明確標示為不安全的根設定診斷端點。 |
| 測試 | 單元、站內整合／HTTP、套件契約、確定性重試／取消測試，以及使用真正 Central／Demo 程序與 packaged Client 的 E2E 情境 A–J。 |
| 文件與演示 | README、設計與決策文件、短入門導讀、完整示範手冊，以及 14 段請求的 `.http` 演示與操作說明。 |

目前跨程序 E2E 使用一個 Central 與一個 Demo。HTTP 演示也使用相同數量的服務，但以 `ProjectReference` 連結 Client，並由人員逐段呼叫、手動重啟；它不取代使用真正 NuGet 套件的 E2E 或完整示範手冊。

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

目前未包含多個 Demo 同時連到同一中央服務的 E2E；逐步 HTTP 演示使用一個 Central 與一個 Demo。多個 Demo 的 E2E 可另行評估，尚非既定擴充範圍，也不是目前 POC 的未完成缺陷。

## 6. 後續維護交接文件

完成一段工作後，更新已完成內容、目前驗證結果與下一個明確步驟。提交識別只引用新 Repo 可查閱的歷史；文件連結使用相對路徑。保留當前狀態即可，避免累積對話紀錄、私人背景或過期的待辦事項。
