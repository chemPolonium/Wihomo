param(
    [string]$Configuration = "Release",
    [string]$Platform = "x64",
    [switch]$Install,
    [switch]$Register
)

# MSIX 需要与清单 Publisher 匹配的证书。首次运行会生成当前用户自签名证书并信任它；
# 正式分发时应换成受信任的代码签名证书。
$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$msixDir = Join-Path $projectRoot "artifacts\msix"
$certDir = Join-Path $projectRoot "artifacts\certs"
$subject = "CN=chemPolonium"

New-Item -ItemType Directory -Force -Path $certDir | Out-Null

$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $subject } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if (-not $cert) {
    $cert = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $subject `
        -KeyUsage DigitalSignature `
        -KeyExportPolicy Exportable `
        -FriendlyName "Wihomo MSIX Dev" `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") `
        -NotAfter (Get-Date).AddYears(5)
    Write-Host "已生成开发签名证书: $($cert.Thumbprint)"
}

$cerPath = Join-Path $certDir "Wihomo.DevCert.cer"
Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null

$trusted = Get-ChildItem Cert:\CurrentUser\TrustedPeople |
    Where-Object { $_.Thumbprint -eq $cert.Thumbprint }
if (-not $trusted) {
    Import-Certificate -FilePath $cerPath -CertStoreLocation "Cert:\CurrentUser\TrustedPeople" | Out-Null
    Write-Host "已将开发证书加入当前用户受信任发布者。"
}

# Windows PowerShell 的参数模式不会展开 `$cert.Thumbprint` 这种成员访问，
# 必须先把指纹存进变量，否则 MSBuild 会收到证书对象的 ToString() 文本。
$thumbprint = $cert.Thumbprint

dotnet publish (Join-Path $projectRoot "Wihomo.csproj") `
    -c $Configuration `
    -p:Platform=$Platform `
    -p:GenerateAppxPackageOnBuild=true `
    -p:AppxPackageSigningEnabled=true `
    -p:PackageCertificateThumbprint=$thumbprint `
    -p:AppxPackageDir="$msixDir\"

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败，退出代码: $LASTEXITCODE"
}

$msix = Get-ChildItem $msixDir -Recurse -Filter *.msix |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $msix) {
    throw "未在 $msixDir 下找到 MSIX 产物。"
}

Write-Host "MSIX: $($msix.FullName)"

function Install-DevLayout {
    param([System.IO.FileInfo]$Package)

    if (Get-Process Wihomo -ErrorAction SilentlyContinue) {
        throw "请先退出正在运行的 Wihomo，再执行 -Register/-Install。"
    }

    $devMode = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock" -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense
    if ($devMode -ne 1) {
        throw "开发模式松散注册需要先开启 Windows 开发者模式。"
    }

    # 重新注册前先备份包数据：Remove-AppxPackage 可能清掉 LocalCache，
    # 其中包含 %AppData%\Wihomo\settings.json 的 MSIX 重定向副本。
    $existing = Get-AppxPackage chemPolonium.Wihomo -ErrorAction SilentlyContinue | Select-Object -First 1
    $backupRoot = Join-Path $env:LOCALAPPDATA "Wihomo\DevBackups\$((Get-Date).ToString('yyyyMMdd-HHmmss'))"
    $packageDataBackup = $null
    $legacyBackup = $null

    if ($existing) {
        $packageData = Join-Path $env:LOCALAPPDATA "Packages\$($existing.PackageFamilyName)"
        if (Test-Path $packageData) {
            $packageDataBackup = Join-Path $backupRoot "package"
            New-Item -ItemType Directory -Path $packageDataBackup -Force | Out-Null
            foreach ($name in @("LocalCache", "LocalState")) {
                $source = Join-Path $packageData $name
                if (Test-Path $source) {
                    Copy-Item $source (Join-Path $packageDataBackup $name) -Recurse -Force
                }
            }
            Write-Host "已备份 MSIX 包数据: $backupRoot"
        }
    }

    $legacySettings = Join-Path $env:APPDATA "Wihomo"
    if (Test-Path $legacySettings) {
        $legacyBackup = Join-Path $backupRoot "legacy-roaming"
        New-Item -ItemType Directory -Path $legacyBackup -Force | Out-Null
        Copy-Item $legacySettings (Join-Path $legacyBackup "Wihomo") -Recurse -Force
        Write-Host "已备份旧版设置: $backupRoot"
    }

    $layout = Join-Path $env:LOCALAPPDATA "Wihomo\DevLayout"
    Get-AppxPackage chemPolonium.Wihomo -ErrorAction SilentlyContinue | Remove-AppxPackage
    Remove-Item $layout -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $layout | Out-Null

    $zip = Join-Path $env:TEMP "wihomo-msix-layout.zip"
    Copy-Item $Package.FullName $zip -Force
    try {
        Expand-Archive -Path $zip -DestinationPath $layout -Force
    }
    finally {
        Remove-Item $zip -Force -ErrorAction SilentlyContinue
    }

    Add-AppxPackage -Register (Join-Path $layout "AppxManifest.xml")

    # 恢复包数据。PackageFamilyName 通常不变；若变化则使用新名称。
    $registered = Get-AppxPackage chemPolonium.Wihomo -ErrorAction SilentlyContinue | Select-Object -First 1
    $familyName = if ($registered) { $registered.PackageFamilyName } elseif ($existing) { $existing.PackageFamilyName } else { $null }
    if ($packageDataBackup -and $familyName) {
        $target = Join-Path $env:LOCALAPPDATA "Packages\$familyName"
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        foreach ($name in @("LocalCache", "LocalState")) {
            $source = Join-Path $packageDataBackup $name
            if (Test-Path $source) {
                $destination = Join-Path $target $name
                New-Item -ItemType Directory -Path $destination -Force | Out-Null
                Copy-Item (Join-Path $source "*") $destination -Recurse -Force
            }
        }
        Write-Host "已恢复 MSIX 包数据。"
    }

    if ($legacyBackup) {
        Copy-Item (Join-Path $legacyBackup "Wihomo") $legacySettings -Recurse -Force
        Write-Host "已恢复旧版设置。"
    }

    Write-Host "已通过开发者模式松散注册 Wihomo: $layout"
    Write-Host "注意：不要删除该目录，否则已安装的应用无法启动。"
}

if ($Install -or $Register) {
    if ($Register) {
        Install-DevLayout -Package $msix
    }
    else {
        try {
            Add-AppxPackage -Path $msix.FullName -ForceUpdateFromAnyVersion
            Write-Host "已安装 Wihomo（签名 MSIX）。"
        }
        catch {
            # 自签名证书默认只进了 CurrentUser\TrustedPeople；部分 Windows 版本
            # 还要求证书位于本机受信任根/受信任人存储（需要管理员权限）。
            Write-Warning "签名 MSIX 安装失败: $($_.Exception.Message)"
            Write-Warning "如需安装签名 MSIX，请用管理员 PowerShell 执行一次："
            Write-Warning "  certutil -addstore -f Root `"$cerPath`""
            Write-Warning "改为开发者模式松散注册..."
            Install-DevLayout -Package $msix
        }
    }
}
