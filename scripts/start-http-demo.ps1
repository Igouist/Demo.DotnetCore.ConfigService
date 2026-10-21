<#
.SYNOPSIS
    一個指令啟動 HTTP 演示的 Central 與兩個 Demo，並在同一個視窗重啟 Demo。

.DESCRIPTION
    搭配 docs/demo/http-demo-guide.md 與 docs/demo/config-distribution-demo.http 使用。

    腳本依序：
      1. 還原並建置方案（可用 -NoBuild 略過）。
      2. 建立本次演示的暫存目錄與新的 SQLite 資料庫，啟動 Central（5180）。
      3. 等你在 .http 執行 01–04 後按 Enter，再啟動 Demo A（5181）與 Demo B（5182）。
      4. 顯示指令選單：輸入 A、B 或 R 重啟 Demo，S 查看狀態，Q 結束。

    結束時（輸入 Q 或按 Ctrl+C）停止本腳本啟動的三個程序，並刪除本次暫存目錄。
    三個程序的輸出寫入暫存目錄的日誌檔，不顯示在此視窗；腳本只摘要 Demo 的啟動結果。

    腳本不寫入任何設定值，也不呼叫 .http 的請求；中央資料完全由 .http 寫入，
    所以 .http 01–17 的版本號與預期結果和手動流程相同。

    支援 Windows PowerShell 5.1 與 PowerShell 7 以上版本。

.PARAMETER NoBuild
    略過 restore 與 build，直接使用既有的 Release 建置輸出。

.PARAMETER KeepData
    結束時保留暫存目錄（資料庫與日誌），並顯示其路徑。

.EXAMPLE
    ./scripts/start-http-demo.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\scripts\start-http-demo.ps1 -NoBuild
#>
[CmdletBinding()]
param(
    [switch] $NoBuild,
    [switch] $KeepData
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 連接埠與中央環境需與 config-distribution-demo.http 頂端的變數一致。
$CentralPort = 5180
$DemoAPort = 5181
$DemoBPort = 5182
$CentralEnvironment = 'HttpDemo'
$ReadyTimeoutSeconds = 30

$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'Demo.DotnetCore.ConfigService.sln'
$centralProject = Join-Path $repoRoot 'src/Demo.DotnetCore.ConfigService.CentralHost'
$demoProject = Join-Path $repoRoot 'src/Demo.DotnetCore.ConfigService.DemoHost'
$centralDll = Join-Path $centralProject 'bin/Release/net8.0/Demo.DotnetCore.ConfigService.CentralHost.dll'
$demoDll = Join-Path $demoProject 'bin/Release/net8.0/Demo.DotnetCore.ConfigService.DemoHost.dll'

function Write-Step([string] $Message) {
    Write-Host ''
    Write-Host "== $Message" -ForegroundColor Cyan
}

# Start-Process 在 Windows PowerShell 5.1 不會替含空白的引數加引號，所以自行組成命令列。
# 規則同 Windows 命令列解析：雙引號前與結尾的反斜線要加倍。
function ConvertTo-ArgumentString([string[]] $Arguments) {
    $quoted = foreach ($argument in $Arguments) {
        $escaped = $argument -replace '(\\*)"', '$1$1\"'
        $escaped = $escaped -replace '(\\+)$', '$1$1'
        '"' + $escaped + '"'
    }
    $quoted -join ' '
}

function Test-PortListening([int] $Port) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $client.Connect('127.0.0.1', $Port)
        return $true
    }
    catch {
        return $false
    }
    finally {
        $client.Close()
    }
}

function New-DemoService([string] $Name, [string] $LogName, [int] $Port, [string[]] $Arguments) {
    [pscustomobject] @{
        Name       = $Name
        LogName    = $LogName
        Port       = $Port
        Arguments  = $Arguments
        Process    = $null
        StartCount = 0
        StdOut     = $null
        StdErr     = $null
    }
}

function Start-DemoProcess($Service) {
    $Service.StartCount++
    $suffix = if ($Service.StartCount -gt 1) { "-$($Service.StartCount)" } else { '' }
    $Service.StdOut = Join-Path $demoSession "$($Service.LogName)$suffix.out.log"
    $Service.StdErr = Join-Path $demoSession "$($Service.LogName)$suffix.err.log"

    $process = Start-Process -FilePath $dotnet `
        -ArgumentList (ConvertTo-ArgumentString $Service.Arguments) `
        -WorkingDirectory $repoRoot `
        -RedirectStandardOutput $Service.StdOut `
        -RedirectStandardError $Service.StdErr `
        -NoNewWindow -PassThru
    # 先取得 Handle，Windows PowerShell 才能在程序結束後讀到 ExitCode。
    $null = $process.Handle
    $Service.Process = $process
}

function Test-DemoProcessRunning($Service) {
    return ($null -ne $Service.Process) -and (-not $Service.Process.HasExited)
}

# 回傳 'ready'（已開始監聽）、'exited'（程序已結束）或 'timeout'。
function Wait-DemoProcessReady($Service) {
    $deadline = (Get-Date).AddSeconds($ReadyTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ($Service.Process.HasExited) { return 'exited' }
        if (Test-PortListening $Service.Port) { return 'ready' }
        Start-Sleep -Milliseconds 300
    }
    return 'timeout'
}

function Stop-DemoProcess($Service) {
    if (Test-DemoProcessRunning $Service) {
        $Service.Process.Kill()
        $null = $Service.Process.WaitForExit(10000)
    }
}

# Demo 在標準錯誤輸出 {"source":"demo-central-config",...} 標記；只顯示這些行，完整內容在日誌檔。
function Write-DemoMarkers($Service) {
    if (-not (Test-Path -LiteralPath $Service.StdErr)) { return }
    Get-Content -LiteralPath $Service.StdErr -Encoding UTF8 |
        Where-Object { $_ -match '"source":"demo-central-config"' } |
        ForEach-Object { Write-Host "   $_" }
}

function Start-DemoAndReport($Service) {
    if (Test-DemoProcessRunning $Service) {
        Write-Host "停止 $($Service.Name)…"
        Stop-DemoProcess $Service
    }
    Write-Host "啟動 $($Service.Name)（http://127.0.0.1:$($Service.Port)）…"
    Start-DemoProcess $Service
    $result = Wait-DemoProcessReady $Service
    Write-DemoMarkers $Service
    switch ($result) {
        'ready' {
            Write-Host "$($Service.Name) 已開始監聽。" -ForegroundColor Green
        }
        'exited' {
            Write-Host "$($Service.Name) 沒有開始監聽，程序已結束（結束代碼 $($Service.Process.ExitCode)）。" -ForegroundColor Yellow
            Write-Host "   完整輸出：$($Service.StdErr)"
        }
        default {
            Write-Host "$($Service.Name) 在 $ReadyTimeoutSeconds 秒內沒有開始監聽，仍在執行。完整輸出：$($Service.StdErr)" -ForegroundColor Yellow
        }
    }
}

function Write-DemoStatus {
    foreach ($service in @($central, $demoA, $demoB)) {
        if ($null -eq $service.Process) {
            $state = '尚未啟動'
        }
        elseif ($service.Process.HasExited) {
            $state = "已結束（結束代碼 $($service.Process.ExitCode)）"
        }
        else {
            $state = "執行中（PID $($service.Process.Id)）"
        }
        Write-Host ("   {0,-8} http://127.0.0.1:{1}  {2}" -f $service.Name, $service.Port, $state)
        if ($null -ne $service.StdErr) {
            Write-Host "            日誌：$($service.StdOut)"
            Write-Host "                  $($service.StdErr)"
        }
    }
}

function Write-Menu {
    Write-Host ''
    Write-Host '指令：A 重啟 Demo A ／ B 重啟 Demo B ／ R 重啟兩個 Demo ／ S 狀態與日誌位置 ／ Q 結束並清理' -ForegroundColor Cyan
}

# ---- 前置檢查 ----

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnetCommand) {
    throw '找不到 dotnet。請安裝 .NET SDK 10.0.x 與 .NET 8 ASP.NET Core 執行階段。'
}
$dotnet = $dotnetCommand.Source

# 環境變數的優先序高於中央快照（見 HANDOFF 第 3 節），會讓 Demo 的結果與 .http 的預期不同。
$overridingVariables = @(Get-ChildItem Env: | Where-Object { $_.Name -match '^Demo(__|:)' })
if ($overridingVariables.Count -gt 0) {
    $names = ($overridingVariables | ForEach-Object { $_.Name }) -join ', '
    throw "目前的工作階段有會蓋過中央值的環境變數：$names。請先移除（例如 Remove-Item Env:\$($overridingVariables[0].Name)），再重新執行。"
}

$busyPorts = @($CentralPort, $DemoAPort, $DemoBPort | Where-Object { Test-PortListening $_ })
if ($busyPorts.Count -gt 0) {
    throw "連接埠 $($busyPorts -join ', ') 已有服務在使用。請先停止該服務，或依 http-demo-guide.md 的說明改用其他連接埠。"
}

if (-not $NoBuild) {
    Write-Step '還原與建置（之後可用 -NoBuild 略過）'
    & $dotnet restore $solution -maxcpucount:1 -nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore 失敗（結束代碼 $LASTEXITCODE）。" }
    & $dotnet build $solution -c Release --no-restore -maxcpucount:1 -nodeReuse:false -p:UsePackagedClient=false
    if ($LASTEXITCODE -ne 0) { throw "dotnet build 失敗（結束代碼 $LASTEXITCODE）。" }
}

foreach ($dll in @($centralDll, $demoDll)) {
    if (-not (Test-Path -LiteralPath $dll)) {
        throw "找不到建置輸出 $dll。請不加 -NoBuild 重新執行。"
    }
}

# ---- 啟動 ----

$demoSession = Join-Path ([IO.Path]::GetTempPath()) ('config-http-demo-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $demoSession | Out-Null
$demoDatabase = Join-Path $demoSession 'central-config.db'
$centralBaseAddress = "http://127.0.0.1:$CentralPort/"

$central = New-DemoService 'Central' 'central' $CentralPort @(
    $centralDll,
    '--contentRoot', $centralProject,
    '--urls', "http://127.0.0.1:$CentralPort",
    '--ConnectionStrings:CentralConfigDatabase', "Data Source=$demoDatabase")

$demoA = New-DemoService 'Demo A' 'demo-a' $DemoAPort @(
    $demoDll,
    '--contentRoot', $demoProject,
    '--urls', "http://127.0.0.1:$DemoAPort",
    '--CentralConfig:Enabled', 'true',
    '--CentralConfig:BaseAddress', $centralBaseAddress,
    '--CentralConfig:Environment', $CentralEnvironment)

# Demo B 只多一個本機覆寫，顯示本機設定仍屬於各應用程式。
$demoB = New-DemoService 'Demo B' 'demo-b' $DemoBPort @(
    $demoDll,
    '--contentRoot', $demoProject,
    '--urls', "http://127.0.0.1:$DemoBPort",
    '--CentralConfig:Enabled', 'true',
    '--CentralConfig:BaseAddress', $centralBaseAddress,
    '--CentralConfig:Environment', $CentralEnvironment,
    '--Demo:LocalOnlyMessage', 'local-only-b')

try {
    Write-Step '啟動 Central'
    Write-Host "本次演示資料：$demoSession"
    Start-DemoProcess $central
    $centralResult = Wait-DemoProcessReady $central
    if ($centralResult -ne 'ready') {
        Write-Host "Central 沒有開始監聽（$centralResult）。輸出如下：" -ForegroundColor Red
        Get-Content -LiteralPath $central.StdOut, $central.StdErr -Encoding UTF8 | ForEach-Object { Write-Host "   $_" }
        throw 'Central 啟動失敗。'
    }
    Write-Host "Central 已開始監聽：http://127.0.0.1:$CentralPort" -ForegroundColor Green

    Write-Step '在 .http 依序執行 01–04'
    Write-Host 'Demo 的必要設定鍵要先由 02 寫入，否則 Demo 會啟動失敗。'
    $answer = Read-Host '完成 01–04 後按 Enter 啟動 Demo A 與 Demo B（輸入 Q 直接結束）'

    if ($answer.Trim().ToUpperInvariant() -ne 'Q') {
        Write-Step '啟動 Demo A 與 Demo B'
        Start-DemoAndReport $demoA
        Start-DemoAndReport $demoB
        Write-Host ''
        Write-Host '兩個 Demo 都開始監聽後，從 .http 的 05 繼續。'

        while ($true) {
            if (-not (Test-DemoProcessRunning $central)) {
                Write-Host "Central 已意外結束（結束代碼 $($central.Process.ExitCode)），請查看 $($central.StdErr)。" -ForegroundColor Red
                break
            }
            Write-Menu
            $command = (Read-Host '>').Trim().ToUpperInvariant()
            switch ($command) {
                'A' { Start-DemoAndReport $demoA }
                'B' { Start-DemoAndReport $demoB }
                'R' { Start-DemoAndReport $demoA; Start-DemoAndReport $demoB }
                'S' { Write-DemoStatus }
                'Q' { }
                ''  { }
                default { Write-Host "不認得的指令：$command" -ForegroundColor Yellow }
            }
            if ($command -eq 'Q') { break }
        }
    }
}
finally {
    # 輸入 Q、Central 意外結束、發生錯誤或按 Ctrl+C 都會走到這裡。
    Write-Host ''
    Write-Host '== 停止 Demo A、Demo B 與 Central' -ForegroundColor Cyan
    foreach ($service in @($demoA, $demoB, $central)) {
        Stop-DemoProcess $service
    }

    if ($KeepData) {
        Write-Host "已保留本次演示資料：$demoSession"
    }
    else {
        # Windows 在程序剛結束時可能仍短暫鎖住資料庫檔案，所以重試幾次。
        for ($attempt = 1; $attempt -le 5 -and (Test-Path -LiteralPath $demoSession); $attempt++) {
            try {
                Remove-Item -LiteralPath $demoSession -Recurse -Force
            }
            catch {
                Start-Sleep -Milliseconds 500
            }
        }
        if (Test-Path -LiteralPath $demoSession) {
            Write-Host "無法刪除 $demoSession，請確認程序已結束後手動刪除。" -ForegroundColor Yellow
        }
        else {
            Write-Host '已刪除本次演示資料。'
        }
    }
}
