[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Directory]::GetParent($PSScriptRoot).FullName
$project = Join-Path $repoRoot 'DesktopShell.csproj'
if (-not (Test-Path -LiteralPath $project)) { throw "Project file not found: $project" }

$dotnetArgs = @('build', $project, '--configuration', $Configuration, '-p:SmokeTest=false')
if ($NoRestore) { $dotnetArgs += '--no-restore' }
Write-Host ('dotnet ' + ($dotnetArgs -join ' '))
& dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }