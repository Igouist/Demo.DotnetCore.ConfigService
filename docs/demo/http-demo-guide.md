# 使用 HTTP 呼叫檔演示集中式設定

這份短劇本搭配 [config-distribution-demo.http](config-distribution-demo.http)，讓你逐步觀察中央設定、兩個使用端共用同一份中央值、啟動快照、必要設定鍵缺值時的啟動失敗，以及版本歷史。它使用一個 Central 和兩個 Demo（A、B），不是自動化測試。

只在本機隔離環境使用非敏感資料。呼叫檔會寫入中央資料庫，但不呼叫 `unsafe/root-configuration`，也不啟動或停止程序。請逐段發送請求，不要使用「執行全部」。

## 1. 準備

需要任一 .NET SDK 10.0.x（見 [`global.json`](../../global.json)）、.NET 8 ASP.NET Core 執行階段、Windows PowerShell 5.1 或 PowerShell 7 以上版本，以及支援 `.http` 請求檔的編輯器或工具。

這個短演示使用 `ProjectReference` 連結 Client。使用真正 NuGet 套件的完整情境由 E2E 測試自動驗證（見 README 的「完整驗證」）；使用端如何引用套件，見 [Client 使用說明](../../src/Demo.DotnetCore.ConfigService.Client/README.md)。

確認本機連接埠 `5180`、`5181`、`5182` 未被其他服務使用；腳本啟動前會檢查，已被使用就停止。選用的第 5 節另外使用 `5183`。如果需要改連接埠，請同步修改 [`scripts/start-http-demo.ps1`](../../scripts/start-http-demo.ps1) 頂端的連接埠，以及 `.http` 頂端的 `centralUrl`、`demoAUrl`、`demoBUrl`、`demoCUrl`。

## 2. 用一個指令啟動 Central

從包含 `Demo.DotnetCore.ConfigService.sln` 的專案根目錄執行：

```powershell
# PowerShell 7（Windows、Linux、macOS）
pwsh ./scripts/start-http-demo.ps1

# Windows PowerShell 5.1；執行原則（Execution Policy）不允許執行腳本時，-ExecutionPolicy Bypass 只對這次執行有效
powershell -ExecutionPolicy Bypass -File .\scripts\start-http-demo.ps1
```

腳本會依序：

1. 還原並建置方案。已建置過可加 `-NoBuild` 略過。
2. 建立本次演示專用的暫存目錄與新的 SQLite 資料庫，讓版本號從 1 開始，並顯示目錄位置。
3. 啟動 Central（`http://127.0.0.1:5180`），等它開始監聽。
4. 停下來等你按 Enter，才啟動兩個 Demo。

三個程序的輸出寫入暫存目錄的日誌檔，不顯示在腳本視窗；腳本只摘要 Demo 的啟動結果。腳本不寫入任何設定值，中央資料完全由 `.http` 寫入，所以 01–17 的版本號與預期結果和手動流程相同。若 Central 沒有開始監聽，腳本會印出 Central 的輸出後結束，先處理錯誤再重試。

看到「完成 01–04 後按 Enter」時，在呼叫檔中依序執行 **01–04**，確認中央就緒、建立 `snapshot-v1`，並查看目前值與分發回應。分發介面只回傳中央值及缺少的設定鍵；它不替使用端產生本機後備值。

## 3. 按 Enter 啟動 Demo A 與 Demo B

必須先完成 **02**，因為 Demo 的必要設定鍵若缺值，啟動就會失敗。兩個 Demo 代表兩個不同的使用端應用程式：它們讀取同一個中央設定鍵，但各自保有本機設定。Demo B 只多了一個本機覆寫 `Demo:LocalOnlyMessage`，用來顯示本機設定仍屬於各應用程式。

按 Enter 後，腳本啟動 Demo A（`5181`）與 Demo B（`5182`），並印出每個 Demo 在標準錯誤輸出的 `{"source":"demo-central-config","event":"loaded",...}` 標記。兩者都顯示「已開始監聽」後，執行 **05–06**。兩者的 `CentralRequiredMessage` 都是中央的 `snapshot-v1`；Demo A 的 `LocalOnlyMessage` 是 `local-only`，Demo B 是 `local-only-b`。

之後腳本顯示指令選單，輸入字母再按 Enter：

| 指令 | 作用 |
| --- | --- |
| `A` | 重啟 Demo A：停止執行中的 Demo A（若有），再以相同設定啟動，並摘要啟動結果。 |
| `B` | 重啟 Demo B。 |
| `R` | 依序重啟 Demo A 與 Demo B。 |
| `S` | 顯示三個程序的狀態與日誌檔位置。 |
| `Q` | 停止三個程序並刪除本次暫存目錄。 |

中央環境 `HttpDemo` 必須與 `.http` 的 `centralEnvironment` 相同。這不是 .NET 主機環境名稱。環境變數的優先序高於中央快照，所以腳本發現工作階段有 `Demo__` 開頭的環境變數（例如 `Demo__CentralRequiredMessage`）時會拒絕啟動，請先移除。

## 4. 依序演示

| 請求 | 操作與觀察 |
| --- | --- |
| 07–09 | 把中央改為 `snapshot-v2`。中央查詢已是新值，但執行中的 Demo A 仍回傳 `snapshot-v1`：設定是啟動快照，不會在執行中變動。 |
| 10–11 | 在腳本輸入 `R`，等兩個 Demo 都顯示「已開始監聽」。兩者重新查詢後都取得 `snapshot-v2`：中央只改一次，所有讀取該設定鍵的應用程式重啟後都會生效。Central 不重啟。 |
| 12–14 | Remove 附加版本 3；目前狀態為 `Removed`、值為 `null`，查詢仍回傳 `200`。歷史保留先前兩次 Set。 |
| 15 | 在腳本輸入 `A`，只重啟 Demo A。Demo A 會在幾秒內啟動失敗，腳本顯示 `"event":"failed"`、`"failureKind":"MissingRequiredKeys"` 與 `"missingRequiredKeys":["Demo:CentralRequiredMessage"]`，接著顯示「沒有開始監聽，程序已結束」與非零結束代碼（Linux 為 134）；未處理例外的完整內容在顯示的日誌檔中。這是刻意的快速失敗：必要設定缺值時，應用程式不會帶著錯誤設定上線。接著執行 **15**，查詢仍在執行的 Demo B，它仍回傳 `snapshot-v2`。 |
| 16–17 | 先 Set 還原為 `snapshot-v3`，產生版本 4，再於腳本輸入 `A` 啟動 Demo A。有效設定應變為 `snapshot-v3`。Demo B 要重啟（輸入 `B`）後才會取得 `snapshot-v3`，可視需要示範。 |

觀眾若問「這個中央環境現在有哪些設定？」，可在任一步驟後執行呼叫檔的**附錄 A** 請求。它只列出設定鍵、版本與狀態，不包含值，也不改變資料，不影響 01–17 的預期結果。

Central 執行期間，也可以用瀏覽器開啟 Swagger UI：`http://127.0.0.1:5180/swagger`。它列出 Central 每個管理與分發端點的請求與回應結構，適合回答「這個 API 要傳什麼、會回什麼」，也能按「Try it out」直接呼叫。要看演示資料時，把 `environment` 填成 `HttpDemo` 並只用查詢端點（列表、`current`、`history`）；在 Swagger UI 對 `HttpDemo` 執行 Set 或 Remove 會增加版本，使後續步驟的版本號與預期不同。請求本文的預設範例使用 `UAT`，不影響演示資料。

每段請求上方都列出預期狀態碼與關鍵欄位，請對照實際回應。`snapshot-v3` 是值的名稱，不是版本號；Remove 也會增加版本。

要查看資料庫內容時，使用 **03**、**13**、**14** 的 `current` 與 `history` 查詢。用 DB Browser for SQLite 或 `sqlite3` 直接開啟資料庫時，部分查詢會顯示 `no such collation sequence: ORDINAL_IGNORE_CASE`，這不表示資料庫損壞；原因與唯讀查詢方式見[用外部 SQLite 工具查看 Central 資料庫](../knowledge/sqlite-external-tool-collation.md)。不要用外部工具寫入資料庫。

若要從頭重做，在腳本輸入 `Q`，再重新執行腳本，它會建立新資料庫。重複 Set 會繼續附加版本，重複 Remove 已移除的設定會回傳 `409`，不能把請求當成不會改變狀態的查詢。

## 5. 選用：設定來源優先序

這段展示中央值不是最終答案：使用端自己的環境變數與命令列仍可覆寫它，優先序由低到高為基底 JSON、主機環境 JSON、中央快照、User Secrets、環境變數、命令列。它另外啟動一個 Demo C（`5183`），不影響 Demo A、B 與 01–17 的預期結果。

中央的 `Demo:CentralRequiredMessage` 必須有值，因為必要設定鍵在中央缺值時，即使本機有覆寫，Demo 仍會啟動失敗。所以請在 **06** 之後、**12** 之前，或 **17** 之後進行，不要在 12–16 之間。

開一個新的 PowerShell 終端機 D，從專案根目錄設定環境變數並啟動 Demo C：

```powershell
$demoDll = 'src/Demo.DotnetCore.ConfigService.DemoHost/bin/Release/net8.0/Demo.DotnetCore.ConfigService.DemoHost.dll'
$demoContentRoot = (Resolve-Path 'src/Demo.DotnetCore.ConfigService.DemoHost').Path
$env:Demo__CentralRequiredMessage = 'environment-value'

dotnet $demoDll `
    --contentRoot "$demoContentRoot" `
    --urls http://127.0.0.1:5183 `
    --CentralConfig:Enabled true `
    --CentralConfig:BaseAddress http://127.0.0.1:5180/ `
    --CentralConfig:Environment HttpDemo
```

執行 **附錄 B**：`CentralRequiredMessage` 是 `environment-value`，環境變數蓋過了中央值。

在終端機 D 按 `Ctrl+C` 停止 Demo C，再加上同名的命令列覆寫啟動：

```powershell
dotnet $demoDll `
    --contentRoot "$demoContentRoot" `
    --urls http://127.0.0.1:5183 `
    --CentralConfig:Enabled true `
    --CentralConfig:BaseAddress http://127.0.0.1:5180/ `
    --CentralConfig:Environment HttpDemo `
    --Demo:CentralRequiredMessage command-value
```

再次執行 **附錄 B**：值變為 `command-value`，命令列又蓋過了環境變數。中央值本身沒有改變，可用 **03** 確認。

完成後在終端機 D 按 `Ctrl+C` 停止 Demo C，並移除環境變數：

```powershell
Remove-Item Env:\Demo__CentralRequiredMessage
```

環境變數只存在於終端機 D 的工作階段。這也是啟動腳本發現 `Demo__` 開頭的環境變數時拒絕啟動的原因：它會讓 Demo A、B 的結果與 `.http` 的預期不同。

## 6. 結束

若進行了第 5 節，先確認終端機 D 的 Demo C 已停止。接著在腳本輸入 `Q`，或按 `Ctrl+C`。腳本會停止它啟動的 Demo A、Demo B 與 Central，再刪除本次的 `config-http-demo-*` 暫存目錄（包含 SQLite 資料庫、可能存在的 WAL／SHM 檔案與日誌）。看到「已刪除本次演示資料」即完成清理。

腳本以強制結束的方式停止程序，不等待正常關機；這對只放非敏感暫存資料的演示沒有影響。若想在結束後查看資料庫或日誌，以 `-KeepData` 啟動腳本，結束時會保留暫存目錄並顯示路徑，看完後自行刪除該目錄。只刪除顯示的確切目錄，不要刪除整個系統暫存目錄。

## 附錄：不使用腳本的手動流程

腳本取代的是上述第 2、3、6 節的手動操作；`.http` 的 01–17 不變。需要逐一看見三個程序的即時輸出，或腳本在某個環境無法執行時，可改用三個終端機手動操作。以下命令皆從專案根目錄執行。

先還原與建置：

```powershell
dotnet restore Demo.DotnetCore.ConfigService.sln -maxcpucount:1 -nodeReuse:false
dotnet build Demo.DotnetCore.ConfigService.sln -c Release --no-restore -maxcpucount:1 -nodeReuse:false -p:UsePackagedClient=false
```

在終端機 A 啟動 Central。每次演示建立獨立暫存目錄與新資料庫，避免更動既有資料，也讓版本號從 1 開始：

```powershell
$demoSession = Join-Path ([IO.Path]::GetTempPath()) ('config-http-demo-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $demoSession | Out-Null
$demoDatabase = Join-Path $demoSession 'central-config.db'
$centralContentRoot = (Resolve-Path 'src/Demo.DotnetCore.ConfigService.CentralHost').Path
Write-Host "本次演示資料：$demoSession"

dotnet src/Demo.DotnetCore.ConfigService.CentralHost/bin/Release/net8.0/Demo.DotnetCore.ConfigService.CentralHost.dll `
    --contentRoot "$centralContentRoot" `
    --urls http://127.0.0.1:5180 `
    --ConnectionStrings:CentralConfigDatabase "Data Source=$demoDatabase"
```

保留此終端機與程序，然後執行 **01–04**。

在終端機 B（位於專案根目錄）啟動 Demo A：

```powershell
$demoContentRoot = (Resolve-Path 'src/Demo.DotnetCore.ConfigService.DemoHost').Path

dotnet src/Demo.DotnetCore.ConfigService.DemoHost/bin/Release/net8.0/Demo.DotnetCore.ConfigService.DemoHost.dll `
    --contentRoot "$demoContentRoot" `
    --urls http://127.0.0.1:5181 `
    --CentralConfig:Enabled true `
    --CentralConfig:BaseAddress http://127.0.0.1:5180/ `
    --CentralConfig:Environment HttpDemo
```

在終端機 C（位於專案根目錄）啟動 Demo B。它只多了一個本機覆寫 `Demo:LocalOnlyMessage`，用來顯示本機設定仍屬於各應用程式：

```powershell
$demoContentRoot = (Resolve-Path 'src/Demo.DotnetCore.ConfigService.DemoHost').Path

dotnet src/Demo.DotnetCore.ConfigService.DemoHost/bin/Release/net8.0/Demo.DotnetCore.ConfigService.DemoHost.dll `
    --contentRoot "$demoContentRoot" `
    --urls http://127.0.0.1:5182 `
    --CentralConfig:Enabled true `
    --CentralConfig:BaseAddress http://127.0.0.1:5180/ `
    --CentralConfig:Environment HttpDemo `
    --Demo:LocalOnlyMessage local-only-b
```

第 4 節的「輸入 `R`」改為在終端機 B、C 各按 `Ctrl+C`，等 Demo 結束後再執行相同的啟動命令；「輸入 `A`」改為只在終端機 B 這樣做。不要加入 `Demo__CentralRequiredMessage` 等環境變數或同名命令列覆寫，也請檢查終端機是否已繼承這些變數。

結束時在終端機 B、C 按 `Ctrl+C` 停止兩個 Demo，再在終端機 A 停止 Central。手動流程不會自動清理。確認三個程序都已結束後，可手動移除終端機 A 顯示的本次 `config-http-demo-*` 目錄，包含 SQLite 資料庫及可能存在的 WAL／SHM 檔案。只移除本次建立的確切目錄，不要刪除整個系統暫存目錄。若終端機 A 仍是啟動 Central 的同一個 PowerShell 工作階段，`$demoSession` 仍指向該目錄，可在終端機 A 執行：

```powershell
Remove-Item -LiteralPath $demoSession -Recurse
Test-Path -LiteralPath $demoSession
```

`Test-Path` 回傳 `False` 即表示已移除。
