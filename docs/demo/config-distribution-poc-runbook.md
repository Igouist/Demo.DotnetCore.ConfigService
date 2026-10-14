# 設定分發 .NET 8 POC 示範手冊

## 安全事項與必要條件

本流程的所有設定值都是合成的示範資料。POC 端點**沒有身分驗證**。unsafe-root 路由刻意不遮蔽設定值，不適用於正式環境；不得貼出、記錄或封存其回應。只能使用 [global.json](../../global.json) 指定的 SDK，並從包含 `Demo.DotnetCore.ConfigService.sln` 的儲存庫根目錄執行。

這是以 PowerShell 為主的手動操作流程，不是自動編排腳本。流程只擁有 Git 忽略的 `artifacts/demo` 下唯一的工作階段目錄，以及本次建立的確切穩定版套件封存檔。它不修改父 shell 的 `$env:`，不停止非本次擁有的程序，也不遞迴刪除 `artifacts` 或使用者套件快取。若手動套件來源已有 `0.1.0`，請停止操作、確認所有權，再清理前一次擁有的執行資料，不要覆寫套件。

以下程式碼是一個可執行的 PowerShell 工作階段。請將完整區塊貼入 PowerShell 7 以上版本；`ArgumentList` 的先行檢查會確認所需的執行階段功能。不要分段執行 `try` 區塊，因為 `finally` 區塊會同時保留主要失敗與清理失敗資訊。

```powershell
$repoRoot = (Resolve-Path -LiteralPath .).Path
$sessionName = 'manual-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N')
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts/demo'))
$runRoot = [IO.Path]::GetFullPath((Join-Path $artifactRoot $sessionName))
$feedPath = Join-Path $artifactRoot 'feed'
$feedLockPath = Join-Path $artifactRoot 'manual-feed.lock'
$packagePath = Join-Path $runRoot 'packages'
$httpCachePath = Join-Path $runRoot 'nuget-http-cache'
$pluginsCachePath = Join-Path $runRoot 'nuget-plugins-cache'
$buildHomePath = Join-Path $runRoot 'build-home'
$centralRuntimePath = Join-Path $runRoot 'central-runtime'
$demoRuntimePath = Join-Path $runRoot 'demo-runtime'
$demoObjPath = Join-Path $runRoot 'demo-obj'
$demoBinPath = Join-Path $runRoot 'demo-bin'
$demoObjWithSeparator = $demoObjPath + [IO.Path]::DirectorySeparatorChar
$demoBinWithSeparator = $demoBinPath + [IO.Path]::DirectorySeparatorChar
$nugetConfigPath = Join-Path $runRoot 'NuGet.Config'
$databasePath = Join-Path $runRoot 'central-config.db'
$centralOutputPath = Join-Path $repoRoot 'src/Demo.DotnetCore.ConfigService.CentralHost/bin/Release/net8.0'
$centralDll = Join-Path $centralOutputPath 'Demo.DotnetCore.ConfigService.CentralHost.dll'
$demoOutputPath = Join-Path $demoBinPath 'Release/net8.0'
$demoDll = Join-Path $demoOutputPath 'Demo.DotnetCore.ConfigService.DemoHost.dll'
$dotnetHostPath = (Get-Command dotnet -CommandType Application).Source
$centralUrl = 'http://127.0.0.1:5180'
$demoUrl = 'http://127.0.0.1:5181'
$centralInstanceId = [Guid]::NewGuid().ToString('N')
$packageVersion = '0.1.0'
$packageArchive = Join-Path $feedPath "Demo.DotnetCore.ConfigService.Client.$packageVersion.nupkg"

if (-not (Test-Path -LiteralPath (Join-Path $repoRoot 'Demo.DotnetCore.ConfigService.sln') -PathType Leaf)) {
    throw 'Run this procedure from the Config Service repository root.'
}
if (-not [IO.Path]::IsPathRooted($dotnetHostPath) -or
    -not (Test-Path -LiteralPath $dotnetHostPath -PathType Leaf)) {
    throw 'dotnet did not resolve to an existing absolute host path.'
}
if (-not ([Diagnostics.ProcessStartInfo]::new().PSObject.Properties.Name -contains 'ArgumentList')) {
    throw 'This PowerShell runtime does not expose ProcessStartInfo.ArgumentList.'
}
if (Test-Path -LiteralPath $runRoot) { throw "Run root already exists: $runRoot" }

function Assert-LoopbackPortAvailable {
    param([string] $Url)
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, ([Uri] $Url).Port)
    try { $listener.Start() }
    catch [Net.Sockets.SocketException] {
        throw "The port in $Url is occupied; change the URL variable without stopping that process."
    } finally { $listener.Stop() }
}
Assert-LoopbackPortAvailable $centralUrl
Assert-LoopbackPortAvailable $demoUrl
$ownedProcesses = [Collections.Generic.List[object]]::new()

if (-not ('DemoRunbookStreamPump' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public sealed class DemoRunbookStreamPump : IDisposable
{
    public const int Limit = 64 * 1024;
    private readonly object gate = new object();
    private readonly Stream stream;
    private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
    private readonly byte[] ring = new byte[Limit];
    private int start;
    private int count;
    public bool Truncated { get; private set; }
    public Task Task { get; private set; }

    public DemoRunbookStreamPump(Stream stream)
    {
        this.stream = stream;
    }

    public void Start()
    {
        Task = DrainAsync();
    }

    public void CancelAndClose()
    {
        cancellation.Cancel();
        stream.Dispose();
    }

    public void ObserveFault()
    {
        if (Task != null)
        {
            Task.ContinueWith(completed => { var ignored = completed.Exception; },
                TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private async Task DrainAsync()
    {
        var buffer = new byte[4096];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellation.Token).ConfigureAwait(false)) != 0)
            {
                Append(buffer, read);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private void Append(byte[] buffer, int length)
    {
        lock (gate)
        {
            if (length >= Limit)
            {
                Buffer.BlockCopy(buffer, length - Limit, ring, 0, Limit);
                start = 0;
                count = Limit;
                Truncated = true;
                return;
            }
            var overflow = Math.Max(0, count + length - Limit);
            if (overflow > 0)
            {
                start = (start + overflow) % Limit;
                count -= overflow;
                Truncated = true;
            }
            var end = (start + count) % Limit;
            var first = Math.Min(length, Limit - end);
            Buffer.BlockCopy(buffer, 0, ring, end, first);
            if (length > first) Buffer.BlockCopy(buffer, first, ring, 0, length - first);
            count += length;
        }
    }

    public string Snapshot()
    {
        lock (gate)
        {
            var bytes = new byte[count];
            var first = Math.Min(count, Limit - start);
            Buffer.BlockCopy(ring, start, bytes, 0, first);
            if (count > first) Buffer.BlockCopy(ring, 0, bytes, first, count - first);
            return (Truncated ? "[... output truncated to trailing 64 KiB ...]" + Environment.NewLine : String.Empty)
                + Encoding.UTF8.GetString(bytes);
        }
    }

    public void Dispose() { cancellation.Dispose(); }
}
'@
}

function New-ChildEnvironment {
    param([string] $OwnedRoot, [switch] $ForBuild)

    $result = [Collections.Generic.Dictionary[string,string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $preserved = @('DOTNET_ROOT', 'DOTNET_ROOT(x86)', 'SystemRoot', 'WINDIR',
        'SSL_CERT_FILE', 'SSL_CERT_DIR')
    if ($ForBuild) {
        $preserved += @('HTTP_PROXY', 'HTTPS_PROXY', 'NO_PROXY',
            'http_proxy', 'https_proxy', 'no_proxy')
    }
    foreach ($name in $preserved) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if (-not [string]::IsNullOrWhiteSpace($value)) { $result[$name] = $value }
    }

    $tempPath = Join-Path $OwnedRoot 'temp'
    $cliHomePath = Join-Path $OwnedRoot 'cli-home'
    New-Item -ItemType Directory -Path $tempPath, $cliHomePath -Force | Out-Null
    $result['HOME'] = $OwnedRoot
    $result['USERPROFILE'] = $OwnedRoot
    $result['TEMP'] = $tempPath
    $result['TMP'] = $tempPath
    $result['TMPDIR'] = $tempPath
    $result['DOTNET_CLI_HOME'] = $cliHomePath
    $result['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $result['DOTNET_NOLOGO'] = '1'
    $result['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = '1'
    if ($ForBuild) {
        $programFiles = Join-Path $OwnedRoot 'program-files'
        $programFilesX86 = Join-Path $OwnedRoot 'program-files-x86'
        $appData = Join-Path $OwnedRoot 'app-data'
        $localAppData = Join-Path $OwnedRoot 'local-app-data'
        New-Item -ItemType Directory -Path $programFiles, $programFilesX86, $appData, $localAppData -Force | Out-Null
        $result['PROGRAMFILES'] = $programFiles
        $result['PROGRAMFILES(X86)'] = $programFilesX86
        $result['APPDATA'] = $appData
        $result['LOCALAPPDATA'] = $localAppData
    }
    return (, $result)
}

function Start-OwnedDotnetProcess {
    param(
        [string] $Name,
        [string] $WorkingDirectory,
        [string[]] $Arguments,
        [System.Collections.IDictionary] $EnvironmentMap
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $dotnetHostPath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment.Clear()
    foreach ($argument in $Arguments) { [void] $startInfo.ArgumentList.Add($argument) }
    foreach ($entry in $EnvironmentMap.GetEnumerator()) {
        $startInfo.Environment[[string] $entry.Key] = [string] $entry.Value
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { $process.Dispose(); throw "$Name did not start." }
    $owned = [pscustomobject]@{
        Name = $Name; Process = $process; StdoutPump = $null; StderrPump = $null
        StdoutPath = Join-Path $runRoot "$Name.stdout.log"; StderrPath = Join-Path $runRoot "$Name.stderr.log"
        ExitCode = $null; Completed = $false; Disposed = $false
    }
    [void] $ownedProcesses.Add($owned)
    $stdoutPump = $null
    $stderrPump = $null
    try {
        $stdoutPump = [DemoRunbookStreamPump]::new($process.StandardOutput.BaseStream)
        $stderrPump = [DemoRunbookStreamPump]::new($process.StandardError.BaseStream)
        $stdoutPump.Start(); $stderrPump.Start()
        $owned.StdoutPump = $stdoutPump
        $owned.StderrPump = $stderrPump
        return $owned
    } catch {
        $owned.StdoutPump = $stdoutPump
        $owned.StderrPump = $stderrPump
        $startFailure = $_.Exception
        try {
            [void] (Complete-OwnedDotnetProcess $owned ([TimeSpan]::Zero) -Stop)
        } catch {
            throw [AggregateException]::new('Owned process startup and cleanup both failed.', @($startFailure, $_.Exception))
        }
        throw $startFailure
    }
}

function Get-OwnedPumpTask {
    param([AllowNull()] $Pump)
    if ($null -eq $Pump -or $null -eq $Pump.Task) { return [Threading.Tasks.Task]::CompletedTask }
    return $Pump.Task
}

function Write-OwnedProcessLogs {
    param([pscustomobject] $Owned)

    $errors = [Collections.Generic.List[Exception]]::new()
    if (-not (Test-Path -LiteralPath $runRoot)) { return (, $errors) }
    if ($null -ne $Owned.StdoutPump) {
        try { [IO.File]::WriteAllText($Owned.StdoutPath, $Owned.StdoutPump.Snapshot()) }
        catch { $errors.Add($_.Exception) }
    }
    if ($null -ne $Owned.StderrPump) {
        try { [IO.File]::WriteAllText($Owned.StderrPath, $Owned.StderrPump.Snapshot()) }
        catch { $errors.Add($_.Exception) }
    }
    return (, $errors)
}

function Wait-OwnedProcessPostKillGrace {
    param([pscustomobject] $Owned, [Threading.Tasks.Task] $AllFinished)

    $grace = [Threading.Tasks.Task]::Delay([TimeSpan]::FromSeconds(10))
    if ([Threading.Tasks.Task]::WhenAny([Threading.Tasks.Task[]]@($AllFinished, $grace)).GetAwaiter().GetResult() -ne $AllFinished) {
        if ($null -ne $Owned.StdoutPump) { $Owned.StdoutPump.CancelAndClose(); $Owned.StdoutPump.ObserveFault() }
        if ($null -ne $Owned.StderrPump) { $Owned.StderrPump.CancelAndClose(); $Owned.StderrPump.ObserveFault() }
        throw "$($Owned.Name) did not stop and drain within its bounded deadline; inspect its owned bounded logs."
    }
}

function Complete-OwnedDotnetProcess {
    param([pscustomobject] $Owned, [TimeSpan] $Timeout, [switch] $Stop)

    if ($Owned.Completed) { return [pscustomobject]@{ ExitCode = $Owned.ExitCode } }
    $operationError = $null
    $completed = $false
    $timedOut = $false
    try {
        $allFinished = [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@(
            $Owned.Process.WaitForExitAsync(), (Get-OwnedPumpTask $Owned.StdoutPump), (Get-OwnedPumpTask $Owned.StderrPump)))
        if ($Stop) {
            try { if (-not $Owned.Process.HasExited) { $Owned.Process.Kill($true) } } catch [InvalidOperationException] {}
            Wait-OwnedProcessPostKillGrace $Owned $allFinished
        } else {
            $deadline = [Threading.Tasks.Task]::Delay($Timeout)
            if ([Threading.Tasks.Task]::WhenAny([Threading.Tasks.Task[]]@($allFinished, $deadline)).GetAwaiter().GetResult() -ne $allFinished) {
                try { if (-not $Owned.Process.HasExited) { $Owned.Process.Kill($true) } } catch [InvalidOperationException] {}
                Wait-OwnedProcessPostKillGrace $Owned $allFinished
                $timedOut = $true
            }
        }
        $allFinished.GetAwaiter().GetResult()
        $completed = $true
        $Owned.ExitCode = $Owned.Process.ExitCode
        $Owned.Completed = $true
        if ($timedOut) { throw "$($Owned.Name) exceeded its process deadline; inspect its owned bounded logs." }
    } catch {
        $operationError = $_.Exception
    } finally {
        $logErrors = Write-OwnedProcessLogs $Owned
        $disposeError = $null
        if ($completed) {
            try {
                if ($null -ne $Owned.StdoutPump) { $Owned.StdoutPump.Dispose() }
                if ($null -ne $Owned.StderrPump) { $Owned.StderrPump.Dispose() }
                $Owned.Process.Dispose()
                $Owned.Disposed = $true
                [void] $ownedProcesses.Remove($Owned)
            } catch {
                $disposeError = $_.Exception
                $Owned.Completed = $false
                $Owned.Disposed = $false
            }
        }
        $allErrors = [Collections.Generic.List[Exception]]::new()
        if ($null -ne $operationError) { $allErrors.Add($operationError) }
        if ($null -ne $disposeError) { $allErrors.Add($disposeError) }
        $allErrors.AddRange($logErrors)
        if ($allErrors.Count -eq 1) { throw $allErrors[0] }
        if ($allErrors.Count -gt 1) { throw [AggregateException]::new('Owned process completion failed.', $allErrors) }
    }
    return [pscustomobject]@{ ExitCode = $Owned.ExitCode }
}

function Invoke-OwnedDotnetCommand {
    param([string] $Name, [string[]] $Arguments, [System.Collections.IDictionary] $EnvironmentMap)

    $owned = Start-OwnedDotnetProcess $Name $repoRoot $Arguments $EnvironmentMap
    $result = Complete-OwnedDotnetProcess $owned ([TimeSpan]::FromMinutes(2))
    if ($result.ExitCode -ne 0) {
        throw "$Name failed with exit code $($result.ExitCode); inspect its owned bounded logs."
    }
}

function Start-CentralProcess {
    $environmentMap = New-ChildEnvironment $centralRuntimePath
    $environmentMap['DOTNET_ENVIRONMENT'] = 'E2E'
    $environmentMap['CentralHealth__InstanceId'] = $centralInstanceId
    Start-OwnedDotnetProcess 'central' $centralOutputPath @(
        $centralDll,
        '--contentRoot', $centralOutputPath,
        '--urls', $centralUrl,
        '--ConnectionStrings:CentralConfigDatabase', "Data Source=$databasePath;Pooling=False"
    ) $environmentMap
}

function Start-DemoProcess {
    param(
        [string] $Name = 'demo',
        [hashtable] $EnvironmentOverrides = @{},
        [hashtable] $CommandLineOverrides = @{}
    )
    foreach ($selectionPath in @('CentralConfig:CentralRequiredKeys', 'CentralConfig:FallbackAllowedKeys')) {
        $invalidEnvironment = $EnvironmentOverrides.Keys | Where-Object {
            $normalized = ([string] $_).Replace('__', ':')
            $normalized.Equals($selectionPath, [StringComparison]::OrdinalIgnoreCase) -or
                $normalized.StartsWith("$selectionPath`:", [StringComparison]::OrdinalIgnoreCase)
        }
        $invalidCommandLine = $CommandLineOverrides.Keys | Where-Object {
            $normalized = ([string] $_).Replace('__', ':')
            $normalized.Equals($selectionPath, [StringComparison]::OrdinalIgnoreCase) -or
                $normalized.StartsWith("$selectionPath`:", [StringComparison]::OrdinalIgnoreCase)
        }
        if ($invalidEnvironment -or $invalidCommandLine) {
            throw 'Key Selection arrays must come only from base appsettings.json.'
        }
    }
    $environmentMap = New-ChildEnvironment $demoRuntimePath
    $environmentMap['DOTNET_ENVIRONMENT'] = 'E2E'
    foreach ($name in $EnvironmentOverrides.Keys) { $environmentMap[$name] = [string] $EnvironmentOverrides[$name] }
    $arguments = [Collections.Generic.List[string]]::new()
    foreach ($item in @($demoDll, '--contentRoot', $demoOutputPath, '--urls', $demoUrl,
        '--CentralConfig:Enabled', 'true', '--CentralConfig:BaseAddress', "$centralUrl/",
        '--CentralConfig:Environment', 'uat')) { [void] $arguments.Add($item) }
    foreach ($name in $CommandLineOverrides.Keys) {
        [void] $arguments.Add("--$name")
        [void] $arguments.Add([string] $CommandLineOverrides[$name])
    }
    Start-OwnedDotnetProcess $Name $demoOutputPath ([string[]] $arguments) $environmentMap
}

function Wait-CentralReady {
    param([pscustomobject] $Owned)
    $client = [Net.Http.HttpClient]::new(); $client.Timeout = [TimeSpan]::FromSeconds(1)
    $readinessUri = "$centralUrl/health/ready"
    $managementUri = "$centralUrl/api/v1/management/configurations/current?environment=UAT&key=Demo%3AReadiness"
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    $ownedProbeReady = $false
    try {
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            if ($Owned.Process.HasExited) { throw 'Central exited before readiness.' }
            if (-not $ownedProbeReady) {
                $response = $null
                $document = $null
                $requestDeadline = $null
                try {
                    $remaining = $deadline - [DateTimeOffset]::UtcNow
                    if ($remaining -le [TimeSpan]::Zero) { throw 'Central readiness deadline elapsed.' }
                    $requestBudget = if ($remaining -lt [TimeSpan]::FromSeconds(1)) { $remaining } else { [TimeSpan]::FromSeconds(1) }
                    $requestDeadline = [Threading.CancellationTokenSource]::new($requestBudget)
                    $response = $client.GetAsync($readinessUri, $requestDeadline.Token).GetAwaiter().GetResult()
                    $document = [System.Text.Json.JsonDocument]::Parse(
                        $response.Content.ReadAsStringAsync().GetAwaiter().GetResult())
                    $members = @(
                        $document.RootElement.EnumerateObject() |
                            ForEach-Object Name |
                            Sort-Object
                    )
                    $status = $document.RootElement.GetProperty('status').GetString()
                    $instanceId = $document.RootElement.GetProperty('instanceId').GetString()
                    $cacheControlValues = @(
                        $response.Headers.NonValidated |
                            Where-Object { $_.Key -ceq 'Cache-Control' } |
                            ForEach-Object Value
                    )
                    $contentTypeValues = @(
                        $response.Content.Headers.NonValidated |
                            Where-Object { $_.Key -ceq 'Content-Type' } |
                            ForEach-Object Value
                    )

                    if (
                        [int] $response.StatusCode -eq 200 -and
                        $contentTypeValues.Count -eq 1 -and
                        $response.Content.Headers.ContentType.MediaType -ceq 'application/json' -and
                        $cacheControlValues.Count -eq 1 -and
                        $cacheControlValues[0] -ceq 'no-store' -and
                        ($members -join ',') -ceq 'instanceId,status' -and
                        $status -ceq 'ready' -and
                        $instanceId -ceq $centralInstanceId
                    ) {
                        $ownedProbeReady = $true
                    }
                } catch [Net.Http.HttpRequestException] {} catch [OperationCanceledException] {}
                finally {
                    if ($null -ne $document) { $document.Dispose() }
                    if ($null -ne $response) { $response.Dispose() }
                    if ($null -ne $requestDeadline) { $requestDeadline.Dispose() }
                }
            }

            if ($ownedProbeReady) {
                if ($Owned.Process.HasExited) { throw 'Central exited before Management readiness proof.' }
                $response = $null
                $document = $null
                $requestDeadline = $null
                try {
                    $remaining = $deadline - [DateTimeOffset]::UtcNow
                    if ($remaining -le [TimeSpan]::Zero) { throw 'Central readiness deadline elapsed.' }
                    $requestBudget = if ($remaining -lt [TimeSpan]::FromSeconds(1)) { $remaining } else { [TimeSpan]::FromSeconds(1) }
                    $requestDeadline = [Threading.CancellationTokenSource]::new($requestBudget)
                    $response = $client.GetAsync($managementUri, $requestDeadline.Token).GetAwaiter().GetResult()
                    $document = [System.Text.Json.JsonDocument]::Parse(
                        $response.Content.ReadAsStringAsync().GetAwaiter().GetResult())
                    if (
                        [int] $response.StatusCode -eq 404 -and
                        $response.Content.Headers.ContentType.MediaType -ceq 'application/problem+json' -and
                        $document.RootElement.GetProperty('code').GetString() -ceq 'configuration_not_found'
                    ) {
                        if (($deadline - [DateTimeOffset]::UtcNow) -le [TimeSpan]::Zero) { throw 'Central readiness deadline elapsed.' }
                        if ($Owned.Process.HasExited) { throw 'Central exited after Management readiness proof.' }
                        return
                    }
                } catch [Net.Http.HttpRequestException] {} catch [OperationCanceledException] {}
                finally {
                    if ($null -ne $document) { $document.Dispose() }
                    if ($null -ne $response) { $response.Dispose() }
                    if ($null -ne $requestDeadline) { $requestDeadline.Dispose() }
                }
            }
            $remaining = $deadline - [DateTimeOffset]::UtcNow
            if ($remaining -le [TimeSpan]::Zero) { break }
            $sleepMilliseconds = [Math]::Min(100, [Math]::Floor($remaining.TotalMilliseconds))
            if ($sleepMilliseconds -gt 0) { Start-Sleep -Milliseconds ([int] $sleepMilliseconds) }
        }
        throw 'Central did not reach matching readiness and Management proof within 15 seconds.'
    } finally { $client.Dispose() }
}

function Wait-DemoReady {
    param([pscustomobject] $Owned)
    $client = [Net.Http.HttpClient]::new(); $client.Timeout = [TimeSpan]::FromSeconds(1)
    $uri = "$demoUrl/api/v1/demo/effective-configuration"; $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    try {
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            if ($Owned.Process.HasExited) { throw 'Demo exited before readiness.' }
            $response = $null
            try {
                $response = $client.GetAsync($uri).GetAwaiter().GetResult()
                if ([int] $response.StatusCode -eq 200 -and $response.Content.Headers.ContentType.MediaType -eq 'application/json') { return }
            } catch [Net.Http.HttpRequestException] {} catch [OperationCanceledException] {}
            finally { if ($null -ne $response) { $response.Dispose() } }
            Start-Sleep -Milliseconds 100
        }
        throw 'Demo did not reach its scoped route within 15 seconds.'
    } finally { $client.Dispose() }
}

function Invoke-JsonRequest {
    param([string] $Method, [string] $Uri, [int] $ExpectedStatus, [AllowNull()] [object] $Body = $null, [switch] $RequireNoStore, [pscustomobject] $OwnedProcess)
    $client = [Net.Http.HttpClient]::new()
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method), $Uri)
    if ($null -ne $Body) {
        $json = $Body | ConvertTo-Json -Compress -Depth 8
        $request.Content = [Net.Http.StringContent]::new($json, [Text.Encoding]::UTF8, 'application/json')
    }
    try {
        $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(5))
        try {
            $send = $client.SendAsync($request, $deadline.Token)
            if ($null -ne $OwnedProcess) {
                $finished = [Threading.Tasks.Task]::WhenAny([Threading.Tasks.Task[]]@($send, $OwnedProcess.Process.WaitForExitAsync())).GetAwaiter().GetResult()
                if ($finished -ne $send) { throw "$Method $Uri could not complete because its owned process exited." }
            }
            $response = $send.GetAwaiter().GetResult()
        } finally { $deadline.Dispose() }
        try {
            if ([int] $response.StatusCode -ne $ExpectedStatus) { throw "$Method $Uri returned status $([int] $response.StatusCode)." }
            $mediaType = $response.Content.Headers.ContentType.MediaType
            if ($mediaType -ne 'application/json') { throw "$Method $Uri returned media type $mediaType." }
            $hasNoStore = $response.Headers.CacheControl.NoStore -eq $true
            if ($RequireNoStore -and -not $hasNoStore) { throw "$Method $Uri omitted Cache-Control: no-store." }
            $parsed = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
            [pscustomobject]@{ StatusCode = [int] $response.StatusCode; MediaType = $mediaType; HasNoStore = $hasNoStore; Body = $parsed }
        } finally { $response.Dispose() }
    } finally { $request.Dispose(); $client.Dispose() }
}
function Set-CentralValue { param([string] $Environment, [string] $Key, [AllowEmptyString()] [string] $Value); Invoke-JsonRequest 'POST' "$centralUrl/api/v1/management/configurations/set" 201 @{ environment = $Environment; key = $Key; value = $Value } -OwnedProcess $centralProcess }
function Remove-CentralValue { param([string] $Environment, [string] $Key); Invoke-JsonRequest 'POST' "$centralUrl/api/v1/management/configurations/remove" 201 @{ environment = $Environment; key = $Key } -OwnedProcess $centralProcess }
function Get-CentralCurrent { param([string] $Environment, [string] $Key); Invoke-JsonRequest 'GET' "$centralUrl/api/v1/management/configurations/current?environment=$([Uri]::EscapeDataString($Environment))&key=$([Uri]::EscapeDataString($Key))" 200 -OwnedProcess $centralProcess }
function Get-CentralHistory { param([string] $Environment, [string] $Key); Invoke-JsonRequest 'GET' "$centralUrl/api/v1/management/configurations/history?environment=$([Uri]::EscapeDataString($Environment))&key=$([Uri]::EscapeDataString($Key))" 200 -OwnedProcess $centralProcess }
function Get-DemoScoped { Invoke-JsonRequest 'GET' "$demoUrl/api/v1/demo/effective-configuration" 200 -RequireNoStore -OwnedProcess $demoProcess }
function Get-DemoUnsafeRoot { Invoke-JsonRequest 'GET' "$demoUrl/api/v1/demo/unsafe/root-configuration" 200 -RequireNoStore -OwnedProcess $demoProcess }
function Assert-EqualValue { param([AllowNull()] $Actual, [AllowNull()] $Expected, [string] $Purpose); if ($Actual -cne $Expected) { throw "$Purpose did not match the expected value." } }
function Assert-Sequence { param([object[]] $Actual, [object[]] $Expected, [string] $Purpose); if ([string]::Join('|', $Actual) -cne [string]::Join('|', $Expected)) { throw "$Purpose did not match the expected sequence." } }
function Assert-ScopedContract {
    param([pscustomobject] $Result)
    Assert-EqualValue $Result.StatusCode 200 'Scoped status'; Assert-EqualValue $Result.MediaType 'application/json' 'Scoped media type'; Assert-EqualValue $Result.HasNoStore $true 'Scoped no-store header'; Assert-EqualValue $Result.Body.scope 'Demo' 'Scoped name'
    Assert-Sequence @($Result.Body.PSObject.Properties.Name | Sort-Object) @('effectiveValues', 'scope') 'Scoped top-level members'
    Assert-Sequence @($Result.Body.effectiveValues.PSObject.Properties.Name | Sort-Object) @('CentralRequiredMessage', 'FallbackAllowedMessage', 'LocalOnlyMessage') 'Scoped effective members'
}

$centralProcess = $null
$demoProcess = $null
$feedLock = $null
$ownsPackageArchive = $false
$primaryError = $null
try {
    New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
    try {
        $feedLock = [IO.File]::Open($feedLockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $lockBytes = [Text.Encoding]::UTF8.GetBytes($sessionName); $feedLock.Write($lockBytes, 0, $lockBytes.Length); $feedLock.Flush($true)
    } catch [IO.IOException] { throw 'Another manual run owns the stable package feed, or its lock needs explicit inspection.' }
    if (Test-Path -LiteralPath $packageArchive) { throw 'The manual 0.1.0 package already exists; do not delete it without confirming ownership.' }
    New-Item -ItemType Directory -Path $runRoot | Out-Null
    New-Item -ItemType Directory -Path $feedPath -Force | Out-Null
    New-Item -ItemType Directory -Path $packagePath | Out-Null

    & $dotnetHostPath --version
    if ($LASTEXITCODE -ne 0) { throw 'SDK version check failed.' }
    & $dotnetHostPath restore Demo.DotnetCore.ConfigService.sln -maxcpucount:1 -nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw 'Solution restore failed.' }
    & $dotnetHostPath build Demo.DotnetCore.ConfigService.sln -c Release --no-restore -maxcpucount:1 -nodeReuse:false -p:EnforceCodeStyleInBuild=true
    if ($LASTEXITCODE -ne 0) { throw 'Release solution build failed.' }
    $ownsPackageArchive = $true
    & $dotnetHostPath pack src/Demo.DotnetCore.ConfigService.Client/Demo.DotnetCore.ConfigService.Client.csproj -c Release --no-build --no-restore --output $feedPath -p:PackageVersion=$packageVersion
    if ($LASTEXITCODE -ne 0) { throw 'Client package creation failed.' }

    $fallbackPackagePath = if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) { Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) '.nuget/packages' } else { [IO.Path]::GetFullPath($env:NUGET_PACKAGES) }
    if (-not (Test-Path -LiteralPath $fallbackPackagePath -PathType Container)) { throw 'The pre-restored fallback package directory does not exist.' }
    $fallbackClientPath = Join-Path $fallbackPackagePath "demo.dotnetcore.configservice.client/$packageVersion"
    if (Test-Path -LiteralPath $fallbackClientPath) { throw 'The shared fallback already contains Client 0.1.0; do not delete it. Use the unique-version automated E2E proof instead.' }
    function Write-NuGetElement { param([System.Xml.XmlWriter] $Writer, [string] $Element, [hashtable] $Attributes); $Writer.WriteStartElement($Element); foreach ($name in $Attributes.Keys) { $Writer.WriteAttributeString([string] $name, [string] $Attributes[$name]) }; $Writer.WriteEndElement() }
    $xmlSettings = [System.Xml.XmlWriterSettings]::new(); $xmlSettings.Indent = $true; $xmlSettings.Encoding = [Text.UTF8Encoding]::new($false)
    $xmlWriter = [System.Xml.XmlWriter]::Create($nugetConfigPath, $xmlSettings)
    try {
        $xmlWriter.WriteStartDocument(); $xmlWriter.WriteStartElement('configuration'); $xmlWriter.WriteStartElement('packageSources')
        Write-NuGetElement $xmlWriter 'clear' @{}; Write-NuGetElement $xmlWriter 'add' @{ key = 'demo-client'; value = $feedPath }; Write-NuGetElement $xmlWriter 'add' @{ key = 'nuget.org'; value = 'https://api.nuget.org/v3/index.json'; protocolVersion = '3' }; $xmlWriter.WriteEndElement()
        $xmlWriter.WriteStartElement('fallbackPackageFolders'); Write-NuGetElement $xmlWriter 'clear' @{}; Write-NuGetElement $xmlWriter 'add' @{ key = 'pre-restored'; value = $fallbackPackagePath }; $xmlWriter.WriteEndElement()
        $xmlWriter.WriteStartElement('packageSourceMapping'); $xmlWriter.WriteStartElement('packageSource'); $xmlWriter.WriteAttributeString('key', 'demo-client'); Write-NuGetElement $xmlWriter 'package' @{ pattern = 'Demo.DotnetCore.ConfigService.Client' }; $xmlWriter.WriteEndElement(); $xmlWriter.WriteStartElement('packageSource'); $xmlWriter.WriteAttributeString('key', 'nuget.org'); Write-NuGetElement $xmlWriter 'package' @{ pattern = '*' }; $xmlWriter.WriteEndElement(); $xmlWriter.WriteEndElement(); $xmlWriter.WriteEndElement(); $xmlWriter.WriteEndElement(); $xmlWriter.WriteEndDocument()
    } finally { $xmlWriter.Dispose() }

    $buildEnvironment = New-ChildEnvironment $buildHomePath -ForBuild
    $buildEnvironment['NUGET_PACKAGES'] = $packagePath; $buildEnvironment['NUGET_HTTP_CACHE_PATH'] = $httpCachePath; $buildEnvironment['NUGET_PLUGINS_CACHE_PATH'] = $pluginsCachePath
    $demoProject = Join-Path $repoRoot 'src/Demo.DotnetCore.ConfigService.DemoHost/Demo.DotnetCore.ConfigService.DemoHost.csproj'
    $commonDemoProperties = @('-maxcpucount:1', '-nodeReuse:false', '-p:UsePackagedClient=true', "-p:DemoClientPackageVersion=$packageVersion", "-p:RestorePackagesPath=$packagePath", "-p:BaseIntermediateOutputPath=$demoObjWithSeparator", "-p:BaseOutputPath=$demoBinWithSeparator", '-p:DefaultItemExcludesInProjectFolder=obj/**')
    Invoke-OwnedDotnetCommand 'demo-restore' (@('restore', $demoProject, '--configfile', $nugetConfigPath) + $commonDemoProperties) $buildEnvironment
    Invoke-OwnedDotnetCommand 'demo-build' (@('build', $demoProject, '-c', 'Release', '--no-restore', '-p:EnforceCodeStyleInBuild=true', '-p:UseSharedCompilation=false') + $commonDemoProperties) $buildEnvironment

    if (-not (Test-Path -LiteralPath $packageArchive -PathType Leaf)) { throw 'The expected stable Client archive was not created.' }
    $assetsPath = Join-Path $demoObjPath 'project.assets.json'; $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json; $libraryKey = "Demo.DotnetCore.ConfigService.Client/$packageVersion"; $library = $assets.libraries.PSObject.Properties[$libraryKey].Value
    if ($library.type -ne 'package') { throw 'Demo did not resolve Client as a package.' }
    $expectedLibraryPath = "demo.dotnetcore.configservice.client/$packageVersion"
    if ($library.path -cne $expectedLibraryPath) { throw 'Demo assets did not resolve the exact isolated Client package path.' }
    $dependencyManifestPath = Join-Path $demoOutputPath 'Demo.DotnetCore.ConfigService.DemoHost.deps.json'; $dependencies = Get-Content -LiteralPath $dependencyManifestPath -Raw | ConvertFrom-Json; $dependency = $dependencies.libraries.PSObject.Properties[$libraryKey].Value
    if ($dependency.type -ne 'package') { throw 'Demo dependency manifest does not identify Client as a package.' }
    $resolvedClientDll = Join-Path $packagePath "demo.dotnetcore.configservice.client/$packageVersion/lib/net8.0/Demo.DotnetCore.ConfigService.Client.dll"
    if (-not (Test-Path -LiteralPath $resolvedClientDll -PathType Leaf)) { throw 'Resolved Client DLL is outside the isolated package path.' }
    if (-not (Test-Path -LiteralPath $demoDll -PathType Leaf)) { throw 'The packaged Demo DLL was not built.' }
    $packageExtractionPath = Join-Path $runRoot 'package-extract'
    if (Test-Path -LiteralPath $packageExtractionPath) { throw 'The owned package extraction path already exists.' }
    [IO.Compression.ZipFile]::ExtractToDirectory($packageArchive, $packageExtractionPath)
    $archiveClientDll = Join-Path $packageExtractionPath 'lib/net8.0/Demo.DotnetCore.ConfigService.Client.dll'
    $outputClientDll = Join-Path $demoOutputPath 'Demo.DotnetCore.ConfigService.Client.dll'
    foreach ($path in @($archiveClientDll, $resolvedClientDll, $outputClientDll)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'A required Client DLL artifact was not found.' }
    }
    $clientHashes = @($archiveClientDll, $resolvedClientDll, $outputClientDll) | ForEach-Object {
        (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash
    }
    if (($clientHashes | Select-Object -Unique).Count -ne 1) {
        throw 'Client DLL hashes do not match across archive, isolated cache, and Demo output.'
    }

    $centralProcess = Start-CentralProcess; Wait-CentralReady $centralProcess
    $requiredReceipt = Set-CentralValue 'UAT' 'demo:centralrequiredmessage' 'case-value'; Assert-EqualValue $requiredReceipt.Body.version 1 'Required version'; Assert-EqualValue $requiredReceipt.Body.state 'Present' 'Required state'
    $demoProcess = Start-DemoProcess -Name 'demo-case'; Wait-DemoReady $demoProcess; $scoped = Get-DemoScoped; Assert-ScopedContract $scoped; Assert-EqualValue $scoped.Body.effectiveValues.CentralRequiredMessage 'case-value' 'Case identity'; Assert-EqualValue $scoped.Body.effectiveValues.FallbackAllowedMessage 'local-fallback' 'Fallback'; [void] (Complete-OwnedDotnetProcess $demoProcess ([TimeSpan]::FromSeconds(15)) -Stop); $demoProcess = $null
    [void] (Set-CentralValue 'uat' 'DEMO:CENTRALREQUIREDMESSAGE' '')
    $demoProcess = Start-DemoProcess -Name 'demo-empty'; Wait-DemoReady $demoProcess; $scoped = Get-DemoScoped; Assert-EqualValue $scoped.Body.effectiveValues.CentralRequiredMessage '' 'Empty string'; [void] (Complete-OwnedDotnetProcess $demoProcess ([TimeSpan]::FromSeconds(15)) -Stop); $demoProcess = $null
    $demoProcess = Start-DemoProcess -Name 'demo-environment' -EnvironmentOverrides @{ Demo__CentralRequiredMessage = 'environment-value' }; Wait-DemoReady $demoProcess; Assert-EqualValue (Get-DemoScoped).Body.effectiveValues.CentralRequiredMessage 'environment-value' 'Environment precedence'; [void] (Complete-OwnedDotnetProcess $demoProcess ([TimeSpan]::FromSeconds(15)) -Stop); $demoProcess = $null
    $demoProcess = Start-DemoProcess -Name 'demo-command' -EnvironmentOverrides @{ Demo__CentralRequiredMessage = 'environment-value' } -CommandLineOverrides @{ 'Demo:CentralRequiredMessage' = 'command-value' }; Wait-DemoReady $demoProcess; Assert-EqualValue (Get-DemoScoped).Body.effectiveValues.CentralRequiredMessage 'command-value' 'Command-line precedence'; [void] (Complete-OwnedDotnetProcess $demoProcess ([TimeSpan]::FromSeconds(15)) -Stop); $demoProcess = $null
    [void] (Set-CentralValue 'UAT' 'Demo:CentralRequiredMessage' 'snapshot-v1')
    $demoProcess = Start-DemoProcess -Name 'demo-snapshot-v1'; Wait-DemoReady $demoProcess; Assert-EqualValue (Get-DemoScoped).Body.effectiveValues.CentralRequiredMessage 'snapshot-v1' 'Initial snapshot'; [void] (Set-CentralValue 'UAT' 'Demo:CentralRequiredMessage' 'snapshot-v2'); Assert-EqualValue (Get-DemoScoped).Body.effectiveValues.CentralRequiredMessage 'snapshot-v1' 'Immutable running snapshot'; [void] (Complete-OwnedDotnetProcess $demoProcess ([TimeSpan]::FromSeconds(15)) -Stop); $demoProcess = Start-DemoProcess -Name 'demo-snapshot-v2' -EnvironmentOverrides @{ Outside__Value = 'outside-value' }; Wait-DemoReady $demoProcess; Assert-EqualValue (Get-DemoScoped).Body.effectiveValues.CentralRequiredMessage 'snapshot-v2' 'Restarted snapshot'
    $historyFirst = Set-CentralValue 'UAT' 'Demo:HistoryMessage' 'history-v1'; $historyRemoved = Remove-CentralValue 'uat' 'demo:historymessage'; $historyRestored = Set-CentralValue 'uAt' 'DEMO:HISTORYMESSAGE' 'history-v3'; Assert-Sequence @($historyFirst.Body.version, $historyRemoved.Body.version, $historyRestored.Body.version) @(1, 2, 3) 'History receipts'
    $history = Get-CentralHistory 'Uat' 'demo:HISTORYmessage'; $current = Get-CentralCurrent 'uAT' 'DEMO:historyMESSAGE'; Assert-Sequence @($history.Body.versions.version) @(3, 2, 1) 'History order'; Assert-Sequence @($history.Body.versions.operation) @('Set', 'Remove', 'Set') 'History operations'; Assert-EqualValue $current.Body.version 3 'Current history version'; Assert-EqualValue $current.Body.state 'Present' 'Current history state'; Assert-EqualValue $current.Body.value 'history-v3' 'Restored history value'; Assert-EqualValue $current.Body.environment $historyFirst.Body.environment 'Environment display spelling'; Assert-EqualValue $current.Body.key $historyFirst.Body.key 'Key display spelling'
    $requiredCurrent = Get-CentralCurrent 'uAt' 'DEMO:CENTRALREQUIREDMESSAGE'; Assert-EqualValue $requiredCurrent.Body.environment $requiredReceipt.Body.environment 'Required Environment casing'; Assert-EqualValue $requiredCurrent.Body.key $requiredReceipt.Body.key 'Required Key casing'
    if ((Read-Host 'Call the unredacted unsafe-root endpoint? Type YES to continue') -ceq 'YES') {
        $unsafe = Get-DemoUnsafeRoot; Assert-EqualValue $unsafe.StatusCode 200 'Unsafe status'; Assert-EqualValue $unsafe.MediaType 'application/json' 'Unsafe media type'; Assert-EqualValue $unsafe.HasNoStore $true 'Unsafe no-store header'; Assert-EqualValue $unsafe.Body.scope 'Root' 'Unsafe scope'; Assert-Sequence @($unsafe.Body.PSObject.Properties.Name | Sort-Object) @('effectiveValues', 'scope', 'warning') 'Unsafe top-level members'; Assert-EqualValue $unsafe.Body.warning 'This response may expose sensitive configuration values.' 'Unsafe warning'; Assert-EqualValue $unsafe.Body.effectiveValues.'Outside:Value' 'outside-value' 'Unsafe root inclusion'
    }
} catch {
    $primaryError = $_.Exception
} finally {
    $cleanupErrors = [Collections.Generic.List[Exception]]::new(); $allProcessesStopped = $true
    $retainedOwnedProcesses = $ownedProcesses.ToArray()
    [Array]::Reverse($retainedOwnedProcesses)
    foreach ($owned in $retainedOwnedProcesses) {
        try { [void] (Complete-OwnedDotnetProcess $owned ([TimeSpan]::FromSeconds(15)) -Stop) } catch { $cleanupErrors.Add($_.Exception) }
        if (-not $owned.Completed -or -not $owned.Disposed) { $allProcessesStopped = $false }
    }
    if ($null -ne $demoProcess -and $demoProcess.Completed -and $demoProcess.Disposed) { $demoProcess = $null }
    if ($null -ne $centralProcess -and $centralProcess.Completed -and $centralProcess.Disposed) { $centralProcess = $null }
    $resolvedArtifactRoot = [IO.Path]::GetFullPath($artifactRoot); $artifactPrefix = $resolvedArtifactRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar; $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot); $safeRunRoot = $resolvedRunRoot.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolvedRunRoot).StartsWith('manual-', [StringComparison]::Ordinal)
    if ($allProcessesStopped) {
        if (-not $safeRunRoot) { $cleanupErrors.Add([InvalidOperationException]::new('Refusing to remove an unowned run root.')) }
        elseif (Test-Path -LiteralPath $resolvedRunRoot) { try { Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force -ErrorAction Stop } catch { $cleanupErrors.Add($_.Exception) } }
        $packageArchive = [IO.Path]::GetFullPath((Join-Path $feedPath "Demo.DotnetCore.ConfigService.Client.$packageVersion.nupkg")); $resolvedFeed = [IO.Path]::GetFullPath($feedPath); $feedPrefix = $resolvedFeed.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $packageArchive.StartsWith($feedPrefix, [StringComparison]::OrdinalIgnoreCase)) { $cleanupErrors.Add([InvalidOperationException]::new('Refusing to remove an unowned package archive.')) }
        elseif ($ownsPackageArchive -and (Test-Path -LiteralPath $packageArchive)) { try { Remove-Item -LiteralPath $packageArchive -Force -ErrorAction Stop; if (-not (Get-ChildItem -LiteralPath $resolvedFeed -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $resolvedFeed -Force -ErrorAction Stop } } catch { $cleanupErrors.Add($_.Exception) } }
    } else { $cleanupErrors.Add([InvalidOperationException]::new('Owned runtime files were retained because a child did not stop and drain cleanly.')) }
    if ($null -ne $feedLock) { try { $feedLock.Dispose(); $feedLock = $null; Remove-Item -LiteralPath $feedLockPath -Force -ErrorAction Stop } catch { $cleanupErrors.Add($_.Exception) } }
    if ($null -ne $primaryError -and $cleanupErrors.Count -gt 0) { $allErrors = [Collections.Generic.List[Exception]]::new(); $allErrors.Add($primaryError); $allErrors.AddRange($cleanupErrors); throw [AggregateException]::new('Demonstration and cleanup both failed.', $allErrors) }
    if ($cleanupErrors.Count -eq 1) { throw $cleanupErrors[0] }
    if ($cleanupErrors.Count -gt 1) { throw [AggregateException]::new('Demonstration cleanup failed.', $cleanupErrors) }
    if ($null -ne $primaryError) { [Runtime.ExceptionServices.ExceptionDispatchInfo]::Capture($primaryError).Throw() }
}
```

## 建置與封裝用戶端套件

工作階段先確認指定的 SDK，再以單一建置節點執行一般方案還原與強制檢查程式碼風格的 Release 建置，最後將 `Demo.DotnetCore.ConfigService.Client` 封裝為穩定版 `0.1.0` 套件。套件來源鎖與起始不存在檢查，可確保不覆寫其他次執行建立的套件。

## 使用已封裝套件還原與建置示範主機

工作階段透過 XML 寫入器寫入 `NuGet.Config`，先清除來源與後備資料夾，再加入本機用戶端套件來源及預先還原的後備來源；只有 `Demo.DotnetCore.ConfigService.Client` 會映射到該套件來源。隔離的示範主機還原與建置使用 `UsePackagedClient=true`、`DemoClientPackageVersion=0.1.0`、獨立的 `RestorePackagesPath`，以及對應的隔離 obj/bin 路徑。重新指定中繼路徑時，`DefaultItemExcludesInProjectFolder=obj/**` 會保留 SDK 的一般排除行為。

為了在 Windows 上可靠地清理，只有隔離的 `demo-build` 加入 `-p:UseSharedCompilation=false`；還原與封裝刻意不加入此參數。流程會驗證預期的穩定版封存檔、`project.assets.json` 中的確切套件路徑，以及 assets 與示範主機 `.deps.json` 中的 `package` 型別。封存檔只能解壓縮到 `runRoot` 下；封存檔內容、隔離套件快取與示範輸出中的用戶端 DLL，必須具有相同的 SHA-256。

## 啟動中央設定主機並確認就緒

中央設定主機是由絕對路徑的 `dotnet` 主機啟動、由本流程擁有的子程序，使用唯一的 SQLite 資料庫與符合允許清單的子程序環境。工作階段為這個手動啟動的中央程序產生一次 `$centralInstanceId`，且只透過子程序環境傳入。嚴格的就緒驗證先要求 `GET /health/ready` 回傳符合啟動識別值的 `200 application/json` 契約，本文只能包含 `status` 與 `instanceId`，原始標頭則只能有一個 `Cache-Control: no-store` 值。接著，同一個 `HttpClient` 必須在原本 15 秒期限的剩餘時間內，確認文件所述的管理介面 `current` 路由回傳 `404 application/problem+json`，且 `code` 為 `configuration_not_found`。流程不使用固定的穩定等待時間。若範例連接埠已被占用，請在開始前修改 `$centralUrl` 或 `$demoUrl`，不要終止占用該連接埠的程序。

每個成功啟動的子程序都會立即登錄，包括隔離的還原、建置，以及中央與示範主機。具時間上限的程序輔助工具會立即開始排空 stdout/stderr。每個串流使用固定大小的位元組環形緩衝區，只保留最後 64 KiB，並在本次擁有的記錄中標示截斷。根程序退出與兩個輸出排空作業共用呼叫端期限；逾時時只終止本次擁有的程序樹，並允許一次共 10 秒、並行進行的終止與排空寬限期。若寬限期到期，工具會取消或關閉兩個輸出讀取作業，在不無限等待的情況下觀察後續讀取失敗，並保留子程序包裝物件與執行根目錄，供最後再次嘗試清理。錯誤只引用本次擁有且容量受限的記錄，不嵌入擷取的輸出。只有確認程序退出且兩個輸出排空作業都完成後，才處置程序控制代碼、從登錄清單移除並清空參照。

## 建立示範資料

操作人員只會呼叫下列公開管理路由：

```text
POST /api/v1/management/configurations/set
POST /api/v1/management/configurations/remove
GET  /api/v1/management/configurations/current
GET  /api/v1/management/configurations/history
```

第一次設定使用 `UAT` 與小寫的 `demo:centralrequiredmessage`；回應版本必須是 `1`，狀態必須是 `Present`。後續合成值與大小寫變更，刻意驗證不區分大小寫的識別行為，同時保留原始顯示拼法。

## 啟動使用已封裝套件的示範主機

示範主機是另一個獨立擁有的子程序，使用以套件模式建置的 DLL 與新的允許清單環境啟動。它使用小寫的 `uat`；設定鍵選擇陣列刻意只允許來自基底 `appsettings.json`，命令列或環境變數對任一陣列的變更都會被拒絕。它在啟動時會間接呼叫 `POST /api/v1/distribution/configurations/resolve`，但這不是操作人員依本手冊發出的請求。

示範主機的就緒條件是限定範圍的公開路由回傳 `200 application/json`。每次重新啟動前，都會先停止、排空輸出、處置並清空前一個擁有的示範程序變數，因此不會有兩個本次擁有的示範程序共用連接埠。

## 展示有效設定

公開的示範路由如下：

```text
GET /api/v1/demo/effective-configuration
```

工作階段要求 `Content-Type: application/json` 與 `Cache-Control: no-store`，範圍必須是 `Demo`，頂層只能有 `effectiveValues` 與 `scope`，相對於 Demo 的設定鍵只能有 `CentralRequiredMessage`、`FallbackAllowedMessage` 與 `LocalOnlyMessage`。它會確認基底檔案中的標準設定鍵選擇取得 `case-value`，從未建立的後備鍵則維持 `local-fallback`。接著儲存空字串、重新啟動示範主機，並確認相對成員仍存在且值為空字串。限定範圍的回應刻意不包含來源或版本成員。

下列穩定的啟動協定僅說明欄位名稱；不得印出標記內容、設定值、要求或缺少的設定鍵、框架訊息、例外或機密。

| 事件 | 確切的 JSON 成員 |
| --- | --- |
| `loaded` | `source`, `event`, `valueCount`, `missingFallbackKeyCount`, `attemptCount`, `elapsedMs` |
| `retry` | `source`, `event`, `attemptCount`, `failureKind`, `httpStatusCode`, `delayMs` |
| `failed` | `source`, `event`, `failureKind`, `attemptCount`, `httpStatusCode`, `elapsedMs` |

## 展示優先序與啟動快照

流程先以本次擁有的子程序環境設定 `Demo__CentralRequiredMessage=environment-value` 重新啟動示範主機，再加上 `--Demo:CentralRequiredMessage command-value` 重新啟動。它會確認環境變數覆寫中央值，而命令列覆寫環境變數；整個過程不修改操作人員 shell 的 `$env:`。

接著，以中央值 `snapshot-v1` 啟動示範主機，在示範程序仍執行時將中央值改為 `snapshot-v2`。流程會確認執行中的程序仍回報 `snapshot-v1`，而停止後重新啟動才回報 `snapshot-v2`，藉此驗證設定是啟動快照，不會在請求時更新。

## 檢查歷史與大小寫識別

工作階段針對獨立的 `Demo:HistoryMessage` 依序執行 `Set history-v1`、`Remove`、`Set history-v3`。操作回執版本必須依序是 `1/2/3`，歷史必須由新到舊排列為 `3/2/1`，操作依序為 `Set/Remove/Set`，目前狀態則必須是 `Present`，值為 `history-v3`。以不同的中央環境與設定鍵大小寫查詢時，必須取得同一個識別項目，且原始中央環境與設定鍵的顯示拼法不得改變。

## 選用的 unsafe-root 示範

只有在提示中明確輸入 `YES` 後，工作階段才會呼叫：

```text
GET /api/v1/demo/unsafe/root-configuration
```

回應必須具有 `Content-Type: application/json`、`Cache-Control: no-store`，範圍必須是 `Root`，頂層只能有 `effectiveValues`、`scope` 與 `warning`，且警告必須完全符合 `This response may expose sensitive configuration values.`。只存在於本次擁有子程序的 `Outside:Value` 檢查，可展示根範圍包含該設定。即使本 POC 使用合成資料，仍須將回應視為敏感內容，不得貼出或封存其本文。

## 停止與清理

`finally` 區塊會擷取共享程序登錄清單的快照，並依啟動順序的反向停止所有保留的子程序；通常先停止示範主機，再停止中央主機，而建置子程序已經完成。若有子程序無法正常停止並排空輸出，流程會保留執行階段檔案。它會驗證工作階段根目錄是 `artifacts/demo` 下名稱以 `manual-` 開頭的子目錄，且只移除該目錄。流程只刪除通過起始不存在檢查後，由本次建立的確切穩定版封存檔，接著釋放並移除套件來源鎖。容量受限記錄的寫入失敗仍會保留，在安全處置完成後，與主要失敗、逾時或清理失敗一起彙整回報。

## 完整自動驗證

完成手動流程後，或獨立從乾淨的儲存庫工作目錄開始，執行下列完整階段驗證命令：

```powershell
dotnet restore Demo.DotnetCore.ConfigService.sln -maxcpucount:1 -nodeReuse:false
dotnet format Demo.DotnetCore.ConfigService.sln --verify-no-changes --no-restore
dotnet build Demo.DotnetCore.ConfigService.sln -c Release --no-restore -maxcpucount:1 -nodeReuse:false -p:EnforceCodeStyleInBuild=true
dotnet test Demo.DotnetCore.ConfigService.sln -c Release --no-build --no-restore -maxcpucount:1 -nodeReuse:false
git diff --check
```

若只有還原步驟因網路受限而出現 `NU1900`，請在取得網路權限後重新執行同一個還原命令。不要放寬套件來源映射，也不要加入使用者套件來源。

針對示範手冊本身的契約，執行：

```powershell
Test-Path -LiteralPath docs/demo/config-distribution-poc-runbook.md
Select-String -LiteralPath docs/demo/config-distribution-poc-runbook.md -Pattern 'UsePackagedClient=true','DemoClientPackageVersion=0.1.0','Demo.DotnetCore.ConfigService.Client','packageSourceMapping','configuration_not_found','Cache-Control','no-store','This response may expose sensitive configuration values.','finally'
git add -N docs/demo/config-distribution-poc-runbook.md
git diff --check
```

另須人工確認本手冊未加入存活或啟動探針路由、身分驗證宣稱、正式環境監控或重啟建議、單一命令腳本、真實認證資料，或父程序環境變更。
