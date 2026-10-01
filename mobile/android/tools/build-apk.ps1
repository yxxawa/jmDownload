# JM Download 手机端 · 构建 APK
#
#   powershell -ExecutionPolicy Bypass -File mobile\android\tools\build-apk.ps1
#
# 默认构建已签名的 Release APK，输出到 mobile/android/dist/。
# 第一次运行会在工具链目录里生成一个自签名密钥（不会写进仓库）。

[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Toolchain = 'C:\jm-mobile-toolchain',
    [string] $OutputDirectory,
    [switch] $DebugBuild,
    # 默认先清理再构建：.NET 10 for Android 的增量构建会产出启动即崩的包
    # （症状是 n_onCreate 找不到实现，看起来像 Mono 运行时没加载）。
    [switch] $Incremental
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot '..\..'))
$project = Join-Path $projectRoot 'JmDownload.Mobile.csproj'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'dist' }
if ($DebugBuild) { $Configuration = 'Debug' }

function Write-Step($message) { Write-Host "==> $message" -ForegroundColor Cyan }

# ── 工具链 ───────────────────────────────────────────────────────────

$jdk = Join-Path $Toolchain 'jdk-21'
$sdk = Join-Path $Toolchain 'sdk'
if (-not (Test-Path (Join-Path $jdk 'bin\java.exe'))) { throw "找不到 JDK：$jdk。请先运行 tools\install-toolchain.ps1" }
if (-not (Test-Path (Join-Path $sdk 'platforms'))) { throw "找不到 Android SDK：$sdk。请先运行 tools\install-toolchain.ps1" }
$env:JAVA_HOME = $jdk
$env:ANDROID_HOME = $sdk
$env:ANDROID_SDK_ROOT = $sdk

# ── 同步共享资源 ─────────────────────────────────────────────────────

Write-Step '生成图标（网页 + Android）'
& node (Join-Path $repoRoot 'mobile\frontend\icons\build-icons.mjs')
if ($LASTEXITCODE -ne 0) { throw '图标生成失败' }

Write-Step '从电脑端源码同步 ArtifactTools'
& node (Join-Path $PSScriptRoot 'sync-backend.mjs')
if ($LASTEXITCODE -ne 0) { throw '后端同步失败' }

# ── 签名密钥 ─────────────────────────────────────────────────────────

$signingArgs = @()
if ($Configuration -eq 'Release') {
    $keystore = Join-Path $Toolchain 'jmd-release.keystore'
    $storePass = 'jmd-mobile-release'
    $alias = 'jmd'
    if (-not (Test-Path $keystore)) {
        Write-Step "生成签名密钥 $keystore"
        & (Join-Path $jdk 'bin\keytool.exe') -genkeypair -v `
            -keystore $keystore -alias $alias `
            -keyalg RSA -keysize 2048 -validity 10000 `
            -storepass $storePass -keypass $storePass `
            -dname "CN=JM Download Mobile, OU=Mobile, O=JM Download, L=, S=, C=CN"
        if ($LASTEXITCODE -ne 0) { throw '密钥生成失败' }
    }
    $signingArgs = @(
        '-p:AndroidKeyStore=true',
        "-p:AndroidSigningKeyStore=$keystore",
        "-p:AndroidSigningStorePass=$storePass",
        "-p:AndroidSigningKeyAlias=$alias",
        "-p:AndroidSigningKeyPass=$storePass"
    )
}

# ── 构建 ─────────────────────────────────────────────────────────────

if (-not $Incremental) {
    Write-Step '清理构建中间产物'
    foreach ($path in @((Join-Path $projectRoot 'bin'), (Join-Path $projectRoot 'obj'))) {
        Remove-Item -Recurse -Force $path -ErrorAction SilentlyContinue
    }
}

$buildArgs = @()
if ($Configuration -eq 'Debug') {
    # Debug 默认走「快速部署」，生成的 APK 单独拷到手机上装不起来，必须把程序集打进包。
    # Release 不能加这个开关，.NET 10 下会产出启动即崩的包。
    $buildArgs += '-p:EmbedAssembliesIntoApk=true'
}

Write-Step "构建 $Configuration APK"
& dotnet build $project -c $Configuration -v minimal @signingArgs @buildArgs
if ($LASTEXITCODE -ne 0) { throw '构建失败' }

# ── 输出 ─────────────────────────────────────────────────────────────

$built = Get-ChildItem (Join-Path $projectRoot "bin\$Configuration") -Recurse -Filter '*Signed.apk' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $built) {
    $built = Get-ChildItem (Join-Path $projectRoot "bin\$Configuration") -Recurse -Filter '*.apk' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
}
if (-not $built) { throw '没有找到生成的 APK' }

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$target = Join-Path $OutputDirectory 'JMDownload-mobile.apk'
Copy-Item -Force $built.FullName $target

$size = [math]::Round((Get-Item $target).Length / 1MB, 1)
Write-Host ''
Write-Host "APK  $target" -ForegroundColor Green
Write-Host "     大小 $size MB · $Configuration"
Write-Host ''
Write-Host '安装：'
Write-Host "  adb install -r `"$target`""
Write-Host '  或把 APK 传到手机后直接点击安装（需要在系统里允许安装未知来源应用）。'
