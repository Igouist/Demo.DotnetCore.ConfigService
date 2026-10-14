# 呼叫端提供的啟動 Logger

本文說明在具體 Host builder 執行 `Build()` 之前工作的 NuGet 套件，如何讓呼叫端選擇是否提供 `ILogger`，並保持啟動診斷可觀測、責任邊界清楚。本文將這個角色稱為 **Caller-supplied Startup Logger（呼叫端提供的啟動 Logger）**。`Bootstrap Logger` 可以作為非正式名稱，但它**不是** `Microsoft.Extensions.Logging` 內建的型別；公開契約使用的仍是 `ILogger`。

這是一個可供套件採用的設計模式，不代表每個套件都必須提供此多載，也不直接決定本 POC 的公開 Interface。專案契約仍應由 Decision Log 或 ADR 決定。

## 模式概要

如果套件選擇支援 Caller-supplied Startup Logger，一種直接的 API 形狀是保留原本的無 Logger 方法，再加入一個呼叫端提供 Logger 的多載（overload）：

```csharp
public static Task AddCentralConfigAsync(
    this IHostApplicationBuilder builder,
    CancellationToken cancellationToken = default);

public static Task AddCentralConfigAsync(
    this IHostApplicationBuilder builder,
    ILogger startupLogger,
    CancellationToken cancellationToken = default);
```

「多載」是同名方法以不同參數清單形成的不同可呼叫成員；編譯器依引數做 overload resolution。其限制與選擇規則由 [C# 語言規格的 overload resolution](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/expressions#1242-overload-resolution)定義。無 Logger 多載讓最小呼叫維持簡單；有 Logger 多載把啟動期間的事件立即交給呼叫端既有的記錄管線。這份 `ILogger` 只代表套件會嘗試發出事件，**不保證**事件已被篩選器、Provider 或最終儲存目的地持久化。

套件不應自行建立或設定 Logging Provider、呼叫 `BuildServiceProvider()`，也不應以 `Console` 當作隱藏後備輸出。呼叫端擁有可觀測性與持久化配置；套件擁有安全、結構化的診斷事件與例外契約。

## 為何發生在 Host `Build()` 之前

`IHostApplicationBuilder` 是組合 Host 所需 configuration、services 與 logging 的 Builder 抽象；其 `Logging` 屬性回傳的是 `ILoggingBuilder`。[`IHostApplicationBuilder` API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.hosting.ihostapplicationbuilder?view=net-8.0)明確將它描述為供加入 logging providers 的集合。應用程式完成組合後，才由 `WebApplicationBuilder` 或 `HostApplicationBuilder` 等具體 Builder 的 `Build()` 建立 Host 與 service provider。[Generic Host](https://learn.microsoft.com/en-us/dotnet/core/extensions/generic-host)

因此，`builder.Logging` 是**設定面**：`ILoggingBuilder` 用來加入 Provider、Filter 或 formatter，並不是可呼叫 `LogInformation` 的事件發射器。相對地，[`ILogger` 介面](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.logging.ilogger?view=net-8.0)才提供 `Log`、scope 與是否啟用某 Log Level 的能力。以下概念不可互換：

```csharp
builder.Logging.AddSimpleConsole(); // 設定未來的 logging pipeline

startupLogger.LogInformation("Central configuration loaded."); // 立即嘗試發射事件
```

中央設定的讀取必須在 `Build()` 前完成，才可把成功取得的不可變 startup snapshot 放到正確的 Configuration Provider 位置；而 terminal failure 又可能使 `Build()` 根本不會發生。這正是預先存在的最終 Host `ILogger` 無法被套件假設為可用的原因。

## 採用此模式時的兩種呼叫方式

不需要即時啟動診斷時，呼叫端只需 await 原本的無 Logger 方法：

```csharp
var builder = WebApplication.CreateBuilder(args);

await builder.AddCentralConfigAsync(cancellationToken);

var app = builder.Build();
```

若套件提供第二個多載，而呼叫端希望在 Host 建立前就有可用 Logger，呼叫端可明確建立一個短生命週期 Factory。`LoggerFactory.Create` 的設定委派拿到 `ILoggingBuilder`；`AddSimpleConsole` 是其官方 extension method。[LoggerFactory 建立與 code configuration](https://learn.microsoft.com/en-us/dotnet/core/extensions/logging/overview#configure-logging-with-code)、[`AddSimpleConsole`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.logging.consoleloggerextensions.addsimpleconsole?view=net-8.0)

```csharp
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

using (var startupLoggerFactory = LoggerFactory.Create(logging =>
    logging.AddSimpleConsole(options => options.SingleLine = true)))
{
    ILogger startupLogger =
        startupLoggerFactory.CreateLogger("CentralConfig.Startup");

    await builder.AddCentralConfigAsync(startupLogger, cancellationToken);
}

var app = builder.Build();
```

若 Logger 已由呼叫端擁有（例如啟動器、測試 harness 或外層組合根所建立），則直接傳入，不轉交所有權也不由套件 dispose：

```csharp
await builder.AddCentralConfigAsync(applicationStartupLogger, cancellationToken);
```

採用此模式的套件可立即呼叫 `LogInformation`、`LogWarning` 或 `LogError`。這些是 `LoggerExtensions` 所提供、最終透過 `ILogger` 發送的便利方法；實際是否記錄仍由呼叫端的 Logger 設定與目的地決定。[.NET logging overview](https://learn.microsoft.com/en-us/dotnet/core/extensions/logging/overview)

一條實用的事件路徑是：成功完成時發一筆 `Information` 摘要；每次即將 retry 時發一筆 `Warning`；確認無法繼續時，在擲出前嘗試發一筆 `Error`。事件的 template 應固定，欄位只取已經安全化的診斷模型，而不是把例外或 response 任意序列化：

```csharp
startupLogger.LogInformation(
    "Central configuration loaded: {EntryCount} entries in {AttemptCount} attempt(s), {ElapsedMs} ms.",
    result.EntryCount, result.AttemptCount, result.Elapsed.TotalMilliseconds);

startupLogger.LogWarning(
    "Central configuration retry {Attempt}; reason {FailureKind}; HTTP status {StatusCode}; delay {DelayMs} ms.",
    retry.Attempt, retry.FailureKind, retry.StatusCode, retry.DelayMilliseconds);

startupLogger.LogError(
    exception,
    "Central configuration load failed: {FailureKind}; attempts {AttemptCount}; elapsed {ElapsedMs} ms.",
    exception.FailureKind, exception.AttemptCount, exception.Elapsed.TotalMilliseconds);
```

Filter 排除事件，或 Provider 正常接受呼叫但沒有把事件持久化，並不會改變載入結果。不過 `ILogger` 是可由呼叫端自行實作的 Interface；Framework 並未保證每個實作或 Provider 都不會從 `Log` 拋出例外。若套件直接呼叫上述方法，logging exception 就可能中斷成功路徑，甚至掩蓋原本準備擲出的 `CentralConfigLoadException`。

因此，套件在採用此模式時還必須明確選擇下列其中一種契約：

1. Logger failure 會傳回呼叫端，並可能使啟動失敗。
2. 診斷不參與控制流程；套件以 safe-emission boundary 隔離 Logger failure，維持原本的載入、取消與 terminal failure 語意。

若選擇第二種契約，概念上的邊界可以如下；正式實作仍應依團隊的例外政策決定是否排除不可恢復的 runtime exception，並以測試鎖定行為：

```csharp
private static void TryEmit(
    ILogger? logger,
    Action<ILogger> emit)
{
    if (logger is null)
    {
        return;
    }

    try
    {
        emit(logger);
    }
    catch (Exception)
    {
        // 不要再使用同一個 Logger 報告 Logger failure。
        // 此邊界刻意避免診斷路徑改寫主要載入結果。
    }
}
```

terminal failure 的順序則是先建立主要例外、嘗試寫 Error，最後仍擲出同一個主要例外：

```csharp
var loadException = CreateCentralConfigLoadException(failure);

TryEmit(startupLogger, logger =>
    logger.LogError(
        loadException,
        "Central configuration load failed: {FailureKind}; attempts {AttemptCount}.",
        loadException.FailureKind,
        loadException.AttemptCount));

throw loadException;
```

safe-emission boundary 也應套用於 `Information` 和 `Warning`，否則三種 level 會有不一致的控制流程。這個邊界會犧牲 Logger failure 本身的直接可見性；可選擇把一個不含敏感資料的失敗標記留在記憶體診斷結果中，但不可遞迴呼叫同一個 Logger。

若 Consumer Application 已有自己的 application-startup `ILogger`，傳入同一個實例會保留其 category、scope、filter 與輸出政策，而不是要求套件猜測新的 category 或目的地。

## 無 Logger 時的成功與失敗

成功路徑沒有提供 Logger 時，實作可以把**已值安全化**的診斷事件暫存為資料，並在 Host 建好且某個明確的呼叫端整合點存在時重播到最終 Logger。這是 buffering/replay 的概念，不是無 Logger 多載已經承諾的輸出機制；若沒有那個整合點，成功摘要可以完全不輸出，且不得改變載入結果。

terminal failure 則不同：`CentralConfigLoadException` 會阻止後續 `Build()`，所以通常沒有最終 Host pipeline 可供重播。若採用 Logger 多載，套件可在 throw 前嘗試一次 package-owned `Error`；如果契約要求 Logger failure 不得掩蓋主要失敗，就必須透過前述 safe-emission boundary 發送。無 Logger 時，例外本身就是可交給呼叫端處理的診斷載體。不要為了補齊這個 Error 而偷偷建立 console/provider。

`AddCentralConfigAsync` 回傳 `Task`。非使用錯誤的非同步例外會存入該 Task，並在 `await` 時重新擲出；呼叫端可在同一個 `try`/`catch` 處理。這是 [TAP 例外規則](https://learn.microsoft.com/en-us/dotnet/standard/asynchronous-programming-patterns/task-based-asynchronous-pattern-tap#exceptions)與 [C# async 例外處理](https://learn.microsoft.com/en-us/dotnet/csharp/asynchronous-programming/#handle-asynchronous-exceptions)的行為。

```csharp
try
{
    await builder.AddCentralConfigAsync(cancellationToken);
}
catch (CentralConfigLoadException exception)
{
    // 這是 Caller 明確選擇的 stderr 輸出，不是套件的隱藏 fallback。
    Console.Error.WriteLine(
        "Central configuration prevented startup: " +
        $"Kind={exception.FailureKind}; " +
        $"Attempts={exception.AttemptCount}; " +
        $"ElapsedMs={exception.Elapsed.TotalMilliseconds}.");

    throw;
}
```

若 `await` 的呼叫端不 catch，例外會沿著啟動呼叫鏈維持未處理狀態，而 Host 也不會建立；Runtime 的預設 handler 會報告未處理例外並終止應用程式，但部署環境是否保存該輸出仍是另一件事。[`AppDomain.UnhandledException`](https://learn.microsoft.com/en-us/dotnet/fundamentals/runtime-libraries/system-appdomain-unhandledexception)呼叫端應將這視為明確的 fail-fast 啟動結果，而不是等待背景工作自行報錯。

取消也應留在這條可 await 的控制流程：將呼叫端的 `CancellationToken` 傳入兩個 overload，並讓取消的 `Task` 以標準 `OperationCanceledException` 語意回到呼叫端。不要把取消改寫成成功、靜默 fallback，或以 `async void` 啟動背景讀取；後者沒有可供呼叫端 await 的 `Task`，外部也無法用一般 `try`/`catch` 觀察其例外。[C# async return types](https://learn.microsoft.com/en-us/dotnet/csharp/asynchronous-programming/async-return-types)

## 安全診斷與責任邊界

`CentralConfigLoadException` 與事件可攜帶有限且可修復問題所需的欄位：`FailureKind`、attempt count、elapsed duration、可選 HTTP status、Central **Environment**、已驗證與清理的 endpoint，以及 terminal 缺少的 Central-Required **Configuration Key** 名稱。成功摘要只記數量、attempt 與耗時；不要列出成功 Key 或 Fallback-Allowed Missing Key。這裡的 Environment、Configuration Key、Central-Required Key 與 Fallback-Allowed Key 均採用專案的[領域詞彙](../../CONTEXT.md)。

不得放入中央或本機的 Configuration Value、connection string、credential、authorization header、token、cookie、完整 request/response body、原始遠端錯誤文字，或未審查的 parser message。安全性要在建立 diagnostic model 時保證，而非先收集再期待 redaction。

可觀測性是「套件以 `ILogger` 嘗試交付安全事件，並以 typed exception 表達失敗」；持久化是「呼叫端的 Provider、filter、sink 與營運系統是否接受並保存事件」。前者可由套件設計，後者不能由套件保證。尤其套件不應在 `IServiceCollection` 組合期間自行 `BuildServiceProvider()` 取得 Logger；官方 DI 指引明確要求避免這種作法，因為它會額外建立 service provider 與生命週期。[避免在設定 services 時呼叫 `BuildServiceProvider`](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/guidelines#recommendations)

同理，套件自行 `LoggerFactory.Create`、`AddSimpleConsole` 或 `Console.WriteLine` 都會越過 Consumer Application 的 logging policy，導致重複、格式/篩選不一致，並可能把本應受控的診斷寫進不預期的地方。Provider 的選擇、輸出位置、保存與 Dispose 是呼叫端責任；套件只接受可選的 `ILogger`，且不擁有它。

## 適用範圍

這個模式可用於任何必須在 Host `Build()` 前完成工作的 NuGet package：遠端設定 startup snapshot、授權或租戶 bootstrap、啟動期 feature metadata 等。是否值得增加公開多載，仍取決於呼叫端是否真的需要即時啟動事件、API 複雜度與相容性成本。前提是套件將工作結果以 Builder 可接受的方式加入、將失敗以可 await 的 `Task` 和明確例外交還，且不假設 Host 已提供可用 `ILogger`。

## 與 Config Distribution POC 的關聯

本文章說明的是呼叫端提供啟動 Logger 的一般技術與責任邊界，不自行構成 POC 契約。Config Distribution POC 採用本文的 overload 與 safe-emission boundary，並直接傳播 Caller-requested cancellation；其 asynchronous builder registration、typed startup diagnostics、Logger ownership、Logger exception isolation 與 cancellation semantics 分別記錄於 [POC Decision Log](../decisions/poc-decision-log.md) 的 POC-039、POC-044、POC-045、POC-046 與 POC-047。

## 第一手資料

- [IHostApplicationBuilder API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.hosting.ihostapplicationbuilder?view=net-8.0)
- [Generic Host](https://learn.microsoft.com/en-us/dotnet/core/extensions/generic-host)
- [ILogger API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.logging.ilogger?view=net-8.0)
- [.NET logging overview](https://learn.microsoft.com/en-us/dotnet/core/extensions/logging/overview)
- [LoggerFactory.Create](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.logging.loggerfactory.create?view=net-8.0)
- [避免在設定 services 時呼叫 BuildServiceProvider](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/guidelines#recommendations)
- [C# async/await 例外處理](https://learn.microsoft.com/en-us/dotnet/csharp/asynchronous-programming/#handle-asynchronous-exceptions)
- [AppDomain.UnhandledException](https://learn.microsoft.com/en-us/dotnet/fundamentals/runtime-libraries/system-appdomain-unhandledexception)
