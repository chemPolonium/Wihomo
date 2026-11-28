param(
    [string]$Configuration = "Debug",
    [string]$Platform = "x64",
    [string]$ExePath
)

# 用一份“用户设置副本”启动调试实例：
# - 读取当前安装版或旧版的 settings.json
# - 复制订阅文件到独立 DevProfile
# - 改写工作目录与端口，关闭系统代理和开机自启
# - 通过 WIHOMO_SETTINGS_PATH 让调试实例只读写副本
# 原始设置、订阅文件、系统代理都不会被修改。

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot

$package = Get-AppxPackage chemPolonium.Wihomo -ErrorAction SilentlyContinue |
    Sort-Object Version -Descending |
    Select-Object -First 1

$sourceSettings = $null
if ($package) {
    $sourceSettings = Join-Path $env:LOCALAPPDATA "Packages\$($package.PackageFamilyName)\LocalCache\Roaming\Wihomo\settings.json"
}

if (-not (Test-Path $sourceSettings)) {
    $sourceSettings = Join-Path $env:APPDATA "Wihomo\settings.json"
}

if (-not (Test-Path $sourceSettings)) {
    throw "找不到用户设置文件。请先运行一次 Wihomo，或确认安装的是 MSIX 版本。"
}

$source = Get-Content $sourceSettings -Raw | ConvertFrom-Json
$sourceWorkingDirectory = $source.Core.WorkingDirectory

$devRoot = Join-Path $env:LOCALAPPDATA "Wihomo\DevProfile"
$devRuntime = Join-Path $devRoot "runtime"
$devSettings = Join-Path $devRoot "settings.json"

New-Item -ItemType Directory -Path $devRuntime -Force | Out-Null

# 只复制订阅与旧版 provider 文件；GEO/config 由内核启动时重新生成。
if ($sourceWorkingDirectory) {
    foreach ($name in @("subscriptions", "proxy_providers")) {
        $from = Join-Path $sourceWorkingDirectory $name
        $to = Join-Path $devRuntime $name
        if (Test-Path $from) {
            Remove-Item $to -Recurse -Force -ErrorAction SilentlyContinue
            Copy-Item $from $to -Recurse -Force
        }
    }
}

$source.Core.WorkingDirectory = $devRuntime
$source.Core.MixedPort = 8090
$source.Core.SocksPort = 8091
$source.Core.HttpPort = 8092
$source.Core.ExternalControllerPort = 9095
$source.Core.EnableSystemProxy = $false
$source.StartWithWindows = $false
$source.StartCoreOnProgramStart = $false

if (-not (Test-Path $source.Core.CoreExecutablePath)) {
    $source.Core.CoreExecutablePath = Join-Path $projectRoot "assets\core\mihomo-windows-amd64-v3.exe"
}

$source | ConvertTo-Json -Depth 20 | Set-Content -Path $devSettings -Encoding UTF8

if (-not $ExePath) {
    $ExePath = Join-Path $projectRoot "bin\$Platform\$Configuration\net10.0-windows10.0.26100.0\win-x64\Wihomo.exe"
}

if (-not (Test-Path $ExePath)) {
    throw "找不到调试程序: $ExePath。请先 dotnet build。"
}

$env:WIHOMO_INSTANCE_SUFFIX = "dev"
$env:WIHOMO_SETTINGS_PATH = $devSettings

Write-Host "调试配置副本: $devSettings"
Write-Host "调试工作目录: $devRuntime"
Write-Host "端口: mixed 8090 / socks 8091 / http 8092 / controller 9095"
Write-Host "系统代理与开机自启已在副本中关闭。"
Write-Host "原始设置不会被修改。"

& cmd.exe /c "start `"`" `"$ExePath`""
