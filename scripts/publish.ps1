[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier,
    [string]$OutputDirectory
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
Write-Host ('dotnet ' + ($dotnetArgs -join ' '))
& dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exe = Join-Path $OutputDirectory 'DesktopShell.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Publish completed but expected GUI executable is missing: $exe" }
Write-Host "Published GUI executable: $exe"