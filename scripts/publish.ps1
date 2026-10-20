[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier,
    [string]$OutputDirectory,
    [switch]$IncludeAllLanguages
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Directory]::GetParent($PSScriptRoot).FullName
$project = Join-Path $repoRoot 'DesktopShell.csproj'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "release\$RuntimeIdentifier"
} elseif (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot $OutputDirectory
}
[System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null

$dotnetArgs = @('publish', $project, '--configuration', 'Release', '--runtime', $RuntimeIdentifier,
                '-p:SmokeTest=false', '--output', $OutputDirectory)
if ($IncludeAllLanguages) { $dotnetArgs += '-p:IncludeAllLanguages=true' }
Write-Host ('dotnet ' + ($dotnetArgs -join ' '))
& dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exe = Join-Path $OutputDirectory 'DesktopShell.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Publish completed but expected GUI executable is missing: $exe" }
$debugSymbols = @(Get-ChildItem -LiteralPath $OutputDirectory -File -Filter '*.pdb')
if ($debugSymbols.Count -ne 0) { throw 'Release output still contains debug symbols (.pdb).' }
$exeMiB = [Math]::Round((Get-Item -LiteralPath $exe).Length / 1MB, 2)
Write-Host "Published GUI executable: $exe"
Write-Host "Executable size: $exeMiB MiB (self-contained, runtime dependencies included)"
if ($IncludeAllLanguages) {
    Write-Host 'Framework messages: all languages'
} else {
    Write-Host 'Framework messages: Simplified Chinese, Traditional Chinese, and English fallback'
}
