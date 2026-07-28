[CmdletBinding()]
param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$publishDirectory = Join-Path $projectRoot "artifacts\publish\win-x64"
$isccPath = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"

if (-not (Test-Path $isccPath)) {
    throw "找不到 Inno Setup 编译器: $isccPath"
}

dotnet publish (Join-Path $projectRoot "Wihomo.csproj") `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --property:BaseOutputPath="$projectRoot\artifacts\build\" `
    --output $publishDirectory

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败，退出代码: $LASTEXITCODE"
}

& $isccPath "/DMyAppVersion=$Version" "/DPublishDir=$publishDirectory" (Join-Path $PSScriptRoot "Wihomo.iss")

if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup 编译失败，退出代码: $LASTEXITCODE"
}
