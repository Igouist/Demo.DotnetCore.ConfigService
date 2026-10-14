# 使用 HTTP 呼叫檔演示集中式設定

這份短劇本搭配 [config-distribution-demo.http](config-distribution-demo.http)，讓你逐步觀察中央設定、使用端的啟動快照與版本歷史。它使用一個 Central 和一個 Demo，不是自動化測試。

只在本機隔離環境使用非敏感資料。呼叫檔會寫入中央資料庫，但不呼叫 `unsafe/root-configuration`，也不啟動或停止程序。請逐段發送請求，不要使用「執行全部」。

## 1. 準備

需要符合 [`global.json`](../../global.json) 的 .NET SDK、.NET 8 ASP.NET Core 執行階段、PowerShell，以及支援 `.http` 請求檔的編輯器或工具。

以下命令皆從包含 `Demo.DotnetCore.ConfigService.sln` 的專案根目錄執行。先還原與建置：

```powershell
dotnet restore Demo.DotnetCore.ConfigService.sln -maxcpucount:1 -nodeReuse:false
dotnet build Demo.DotnetCore.ConfigService.sln -c Release --no-restore -maxcpucount:1 -nodeReuse:false -p:UsePackagedClient=false
```

這個短演示使用 `ProjectReference` 連結 Client。若要驗證真正的 NuGet 封裝、完整情境與安全自動清理，請使用 [完整示範手冊](config-distribution-poc-runbook.md)；不要在手冊結束後直接執行本呼叫檔，因為手冊會停止它啟動的服務。

確認本機連接埠 `5180`、`5181` 未被其他服務使用。如果需要改連接埠，請同步修改下列啟動命令與 `.http` 頂端的 `centralUrl`、`demoUrl`。

## 2. 在終端機 A 啟動 Central

每次演示建立獨立暫存目錄與新資料庫，避免更動既有資料，也讓版本號從 1 開始：

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

保留此終端機與程序。若啟動失敗，先處理錯誤，不要改用同一位址上其他服務的回應繼續演示。

在呼叫檔中依序執行 **01–04**，確認中央就緒、建立 `snapshot-v1`，並查看目前值與分發回應。分發介面只回傳中央值及缺少的設定鍵；它不替使用端產生本機後備值。

## 3. 在終端機 B 啟動 Demo

必須先完成 **02**，因為 Demo 的必要設定鍵若缺值，啟動就會失敗。在另一個位於專案根目錄的終端機執行：

```powershell
$demoContentRoot = (Resolve-Path 'src/Demo.DotnetCore.ConfigService.DemoHost').Path

dotnet src/Demo.DotnetCore.ConfigService.DemoHost/bin/Release/net8.0/Demo.DotnetCore.ConfigService.DemoHost.dll `
    --contentRoot "$demoContentRoot" `
    --urls http://127.0.0.1:5181 `
    --CentralConfig:Enabled true `
    --CentralConfig:BaseAddress http://127.0.0.1:5180/ `
    --CentralConfig:Environment HttpDemo
```

中央環境 `HttpDemo` 必須與 `.http` 的 `centralEnvironment` 相同。這不是 .NET 主機環境名稱。不要加入 `Demo__CentralRequiredMessage` 等環境變數或同名命令列覆寫，否則它們會蓋過中央值；也請檢查終端機是否已繼承這些變數。

等 Demo 顯示已開始監聽後，執行 **05**。有效設定應包含中央的 `snapshot-v1`，以及本機的 `local-fallback`、`local-only`。

## 4. 依序演示

| 請求 | 操作與觀察 |
| --- | --- |
| 06–08 | 把中央改為 `snapshot-v2`。中央查詢已是新值，但執行中的 Demo 仍回傳 `snapshot-v1`。 |
| 09 | 在終端機 B 按 `Ctrl+C`，等 Demo 結束，再執行相同啟動命令。重新查詢後取得 `snapshot-v2`。不要重啟 Central。 |
| 10–12 | Remove 附加版本 3；目前狀態為 `Removed`、值為 `null`，查詢仍回傳 `200`。歷史保留先前兩次 Set。此時執行中的 Demo 仍持有 `snapshot-v2`；暫時不要重啟它。 |
| 13–14 | 先 Set 還原為 `snapshot-v3`，產生版本 4，再重啟 Demo。有效設定應變為 `snapshot-v3`。 |

每段請求上方都列出預期狀態碼與關鍵欄位，請對照實際回應。`snapshot-v3` 是值的名稱，不是版本號；Remove 也會增加版本。

若要從頭重做，先停止兩個程序，再從第 2 節建立新資料庫。重複 Set 會繼續附加版本，重複 Remove 已移除的設定會回傳 `409`，不能把請求當成不會改變狀態的查詢。

## 5. 結束

在終端機 B 按 `Ctrl+C` 停止 Demo，再在終端機 A 停止 Central。這個演示不會自動清理。

確認兩個程序都已結束後，可手動移除終端機 A 顯示的本次 `config-http-demo-*` 目錄，包含 SQLite 資料庫及可能存在的 WAL／SHM 檔案。只移除本次建立的確切目錄，不要刪除整個系統暫存目錄。
