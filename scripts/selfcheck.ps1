[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExecutablePath,
    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'
if (-not [System.IO.Path]::IsPathRooted($ExecutablePath)) {
    $ExecutablePath = [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $ExecutablePath))
}
if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) { throw "Executable not found: $ExecutablePath" }
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path ([System.IO.Path]::GetDirectoryName($ExecutablePath)) 'selfcheck-report.json'
} elseif (-not [System.IO.Path]::IsPathRooted($ReportPath)) {
    $ReportPath = [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $ReportPath))
}
$reportDir = [System.IO.Path]::GetDirectoryName($ReportPath)
if (-not [string]::IsNullOrWhiteSpace($reportDir)) { [System.IO.Directory]::CreateDirectory($reportDir) | Out-Null }

Write-Host "Running packaged-app self-check: $ExecutablePath --selfcheck $ReportPath"
$argumentLine = '--selfcheck "' + $ReportPath + '"'
$process = Start-Process -FilePath $ExecutablePath -ArgumentList $argumentLine -WorkingDirectory ([System.IO.Path]::GetDirectoryName($ExecutablePath)) -PassThru -Wait
$exitCode = $process.ExitCode
Write-Host "PROCESS_EXIT_CODE=$exitCode"
if ($exitCode -ne 0) { throw "Self-check process exited with code $exitCode. Report: $ReportPath" }
if (-not (Test-Path -LiteralPath $ReportPath -PathType Leaf)) { throw "Self-check did not write a report: $ReportPath" }
$report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
if ($report.result -ne 'PASS') { throw "Self-check result was '$($report.result)'. Report: $ReportPath" }
Write-Host 'SELF_CHECK=PASS'
Write-Host "REPORT=$ReportPath"
