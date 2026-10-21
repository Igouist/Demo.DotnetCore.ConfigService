# Demo.DotnetCore.ConfigService.Client

用於設定分發 POC 的 .NET 8 用戶端套件，僅在應用程式啟動時載入中央設定。

> 此 POC 不適用於正式環境。它不提供身分驗證、授權、認證資料、強制 TLS、執行階段更新、離線快取或正式環境維運限制。請在隔離環境中使用非敏感的示範設定。

## 引用套件

POC 不發佈到公開或公司內部的套件來源。從本儲存庫的根目錄封裝到本機資料夾：

```powershell
dotnet pack src/Demo.DotnetCore.ConfigService.Client/Demo.DotnetCore.ConfigService.Client.csproj -c Release -p:PackageVersion=0.1.0 -o artifacts/feed
```

在使用端專案旁放一份 `NuGet.Config`，把該資料夾加為套件來源。`packageSourceMapping` 讓這個套件只從本機資料夾還原，不會誤用其他來源上同名的套件；其餘套件仍從 nuget.org 還原：

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="config-client" value="path/to/artifacts/feed" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="config-client">
      <package pattern="Demo.DotnetCore.ConfigService.Client" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

使用端專案的目標架構需為 `net8.0` 以上，並加入：

```xml
<PackageReference Include="Demo.DotnetCore.ConfigService.Client" Version="0.1.0" />
```

NuGet 會以版本號快取已還原的套件。修改 Client 後若以相同版本號重新封裝，使用端仍會用到快取中的舊版；請改用新的版本號，或先移除全域套件資料夾（`dotnet nuget locals global-packages --list` 顯示位置）中的 `demo.dotnetcore.configservice.client/0.1.0`。

## 註冊方式

建立主機建構器（Host builder）後、呼叫 `Build()` 前，選擇一個多載呼叫，並以 await 等待完成。只能註冊一次：

```csharp
using Demo.DotnetCore.ConfigService.Client;

var builder = WebApplication.CreateBuilder(args);
await builder.AddCentralConfigAsync(cancellationToken);
var app = builder.Build();
```

若要在 `Build()` 前接收不含設定值的事件，請傳入呼叫端擁有的 `ILogger`：

```csharp
await builder.AddCentralConfigAsync(startupLogger, cancellationToken);
```

套件不建立、擁有或處置該 Logger。

啟用中央設定時，套件負責的啟動工作從進入註冊方法起，共有保守的 30 秒總時間預算。套件的終止失敗以 `CentralConfigLoadException` 回報，並提供具型別的 `FailureKind`、`AttemptCount`、`Elapsed`、`HttpStatusCode`、`Environment` 與 `MissingRequiredKeys` 診斷資訊。若中央以 Problem Details 回應最終的 HTTP 失敗，`CentralErrorCode`（例如 `persistence_failure`）與 `CentralTraceId` 會帶出中央的錯誤代碼與追蹤識別碼；回報問題時提供 `CentralTraceId`，中央維護人員即可找到對應的伺服器日誌。呼叫端取消仍以 `OperationCanceledException` 回報，保留原始取消權杖，且不發出終止失敗的 Error 事件。

選用的 Logger 接收不含設定值的結構化事件：成功插入設定提供者後發出 Information，安排重試時發出 Warning，遇到終止失敗的 `CentralConfigLoadException` 時嘗試發出一次 Error。Warning 與 Error 都帶有 `CentralErrorCode` 與 `CentralTraceId` 欄位，值來自該次回應的 Problem Details，沒有時為 null；中央的每筆錯誤日誌因此都能對應到使用端的一筆記錄。不記錄設定值、原始回應本文、認證資料、解析器細節，或缺少中央值的允許後備設定鍵名稱。Logger 失敗會被隔離，不影響啟動結果。不提供 Logger 的多載不保證產生記錄。

## 基底 appsettings.json

兩個設定鍵選擇陣列只能宣告在基底 `appsettings.json`：

```json
{
  "CentralConfig": {
    "Enabled": true,
    "BaseAddress": "https://internal.example/config-center/",
    "Environment": "UAT",
    "CentralRequiredKeys": [
      "Demo:CentralRequiredMessage"
    ],
    "FallbackAllowedKeys": [
      "Demo:FallbackAllowedMessage"
    ]
  }
}
```

必須明確設定 `Enabled`。`false` 代表刻意選擇只使用本機設定，套件不執行中央載入。啟用時，中央系統失敗或回應無效都會使啟動失敗；只有有效回應中個別缺少中央值的允許後備設定鍵，才適用後備行為。

中央設定提供者是不可變的啟動快照，插入在 `appsettings.{HostEnvironment}.json` 之後。User Secrets、環境變數、命令列引數及後續加入的來源仍具有較高優先序。重新啟動使用端應用程式，才能讀取中央設定的變更。程式內以 `IConfiguration[key] = value` 寫入的值（例如 `builder.WebHost.UseUrls(...)`）照常生效，只存在於該程序的記憶體中，不會寫回中央。
