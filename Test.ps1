<#
.SYNOPSIS
Runs the XtraSync test suite and enforces coverage of application logic.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateRange(0, 100)]
    [double]$MinimumLineCoverage = 98,
    [ValidateRange(0, 100)]
    [double]$MinimumBranchCoverage = 95
)

$ErrorActionPreference = 'Stop'
$solutionPath = Join-Path $PSScriptRoot 'XtraSync.slnx'
$settingsPath = Join-Path $PSScriptRoot 'tests\coverage.runsettings'
$resultsRoot = Join-Path $PSScriptRoot 'TestResults'
$runDirectory = Join-Path $resultsRoot ([Guid]::NewGuid().ToString('N'))

& dotnet test $solutionPath --configuration $Configuration -m:1 --disable-build-servers --collect 'XPlat Code Coverage' --settings $settingsPath --results-directory $runDirectory
if ($LASTEXITCODE -ne 0) {
    throw "XtraSync tests failed with exit code $LASTEXITCODE."
}

$reports = @(Get-ChildItem -LiteralPath $runDirectory -Recurse -Filter 'coverage.cobertura.xml')
if ($reports.Count -ne 1) {
    throw "Expected one coverage report, found $($reports.Count) in $runDirectory."
}

[xml]$coverage = Get-Content -LiteralPath $reports[0].FullName -Raw
$culture = [System.Globalization.CultureInfo]::InvariantCulture
$lineCoverage = 100 * [double]::Parse($coverage.coverage.'line-rate', $culture)
$branchCoverage = 100 * [double]::Parse($coverage.coverage.'branch-rate', $culture)
$latestReport = Join-Path $resultsRoot 'coverage.cobertura.xml'
Copy-Item -LiteralPath $reports[0].FullName -Destination $latestReport -Force

Write-Host ('Line coverage: {0:F2}% (minimum {1:F2}%)' -f $lineCoverage, $MinimumLineCoverage)
Write-Host ('Branch coverage: {0:F2}% (minimum {1:F2}%)' -f $branchCoverage, $MinimumBranchCoverage)
Write-Host "Coverage report: $latestReport"

if ($lineCoverage -lt $MinimumLineCoverage -or $branchCoverage -lt $MinimumBranchCoverage) {
    throw 'XtraSync coverage is below the required thresholds.'
}
