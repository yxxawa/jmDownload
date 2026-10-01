# JM Download 手机端 · 本地构建工具链安装
#
# 安装 JDK 21 与 Android SDK（cmdline-tools / platform-tools / build-tools / platform），
# 全部装到 mobile/android/toolchain 目录下，不改动系统环境变量，可整个目录删除。
#
#   powershell -ExecutionPolicy Bypass -File mobile\android\tools\install-toolchain.ps1

[CmdletBinding()]
param(
    [string] $Root,
    [string] $JdkVersion = '21',
    [string] $BuildTools = '35.0.0',
    [string] $Platform = 'android-35',
    [string] $CmdlineTools = '13114758_latest'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if (-not $Root) { $Root = 'C:\jm-mobile-toolchain' }
$Root = [System.IO.Path]::GetFullPath($Root)
$Downloads = Join-Path $Root 'downloads'
New-Item -ItemType Directory -Force -Path $Root, $Downloads | Out-Null

function Write-Step($message) { Write-Host "==> $message" -ForegroundColor Cyan }

function Expand-Zip($zip, $destination) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $destination)
}

function Get-File($uri, $target) {
    if (Test-Path $target) { Write-Host "    已存在：$(Split-Path $target -Leaf)"; return }
    Write-Host "    下载 $uri"
    $tmp = "$target.part"
    Invoke-WebRequest -Uri $uri -OutFile $tmp -UseBasicParsing
    Move-Item -Force $tmp $target
}

# ── JDK ──────────────────────────────────────────────────────────────

$jdkDir = Join-Path $Root "jdk-$JdkVersion"
if (-not (Test-Path (Join-Path $jdkDir 'bin\java.exe'))) {
    Write-Step "安装 JDK $JdkVersion"
    $api = "https://api.adoptium.net/v3/assets/latest/$JdkVersion/hotspot?architecture=x64&image_type=jdk&os=windows&vendor=eclipse"
    $asset = (Invoke-RestMethod -Uri $api) | Select-Object -First 1
    $zip = Join-Path $Downloads $asset.binary.package.name
    Get-File $asset.binary.package.link $zip
    $extract = Join-Path $Root 'jdk-extract'
    Remove-Item -Recurse -Force $extract -ErrorAction SilentlyContinue
    Expand-Zip $zip $extract
    $inner = Get-ChildItem $extract -Directory | Select-Object -First 1
    Remove-Item -Recurse -Force $jdkDir -ErrorAction SilentlyContinue
    Move-Item $inner.FullName $jdkDir
    Remove-Item -Recurse -Force $extract -ErrorAction SilentlyContinue
} else {
    Write-Step "JDK $JdkVersion 已安装"
}
$env:JAVA_HOME = $jdkDir
$env:PATH = "$jdkDir\bin;$env:PATH"
& "$jdkDir\bin\java.exe" -version

# ── Android 命令行工具 ───────────────────────────────────────────────

$sdk = Join-Path $Root 'sdk'
$sdkManager = Join-Path $sdk 'cmdline-tools\latest\bin\sdkmanager.bat'
if (-not (Test-Path $sdkManager)) {
    Write-Step '安装 Android cmdline-tools'
    $zip = Join-Path $Downloads "commandlinetools-win-$CmdlineTools.zip"
    Get-File "https://dl.google.com/android/repository/commandlinetools-win-$CmdlineTools.zip" $zip
    $extract = Join-Path $Root 'cmdline-extract'
    Remove-Item -Recurse -Force $extract -ErrorAction SilentlyContinue
    Expand-Zip $zip $extract
    $target = Join-Path $sdk 'cmdline-tools\latest'
    Remove-Item -Recurse -Force $target -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
    Move-Item (Join-Path $extract 'cmdline-tools') $target
    Remove-Item -Recurse -Force $extract -ErrorAction SilentlyContinue
} else {
    Write-Step 'Android cmdline-tools 已安装'
}

$env:ANDROID_HOME = $sdk
$env:ANDROID_SDK_ROOT = $sdk

Write-Step '接受 SDK 许可'
$yes = ("y`n" * 60)
$yes | & $sdkManager --sdk_root=$sdk --licenses 2>&1 | Out-Null

Write-Step "安装 platform-tools / build-tools;$BuildTools / platforms;$Platform"
& $sdkManager --sdk_root=$sdk "platform-tools" "build-tools;$BuildTools" "platforms;$Platform" 2>&1 |
    ForEach-Object { if ($_ -match 'Warning|Error|error') { Write-Host "    $_" } }

Write-Step '工具链就绪'
Write-Host "    JAVA_HOME    = $jdkDir"
Write-Host "    ANDROID_HOME = $sdk"
Write-Host ''
Write-Host '下一步：'
Write-Host "    `$env:JAVA_HOME='$jdkDir'; `$env:ANDROID_HOME='$sdk'"
Write-Host '    powershell -ExecutionPolicy Bypass -File mobile\android\tools\build-apk.ps1'
