[CmdletBinding()]
param(
    [string] $ResultsDirectory,
    [ValidateRange(0, 100)]
    [double] $MinimumLinePercent = 80,
    [ValidateRange(0, 100)]
    [double] $MinimumBranchPercent = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $ResultsDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/TestResults'
}

$reports = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter 'coverage.cobertura.xml' -Recurse -File)
if ($reports.Count -eq 0) {
    throw "No Cobertura reports were found under '$ResultsDirectory'."
}

$culture = [System.Globalization.CultureInfo]::InvariantCulture
foreach ($report in $reports) {
    [xml] $document = Get-Content -LiteralPath $report.FullName -Raw
    $linePercent = [double]::Parse($document.coverage.'line-rate', $culture) * 100
    $branchPercent = [double]::Parse($document.coverage.'branch-rate', $culture) * 100

    Write-Host ("Coverage {0}: line={1:N2}% branch={2:N2}%" -f $report.FullName, $linePercent, $branchPercent)

    if ($linePercent -lt $MinimumLinePercent) {
        throw ("Line coverage {0:N2}% is below the {1:N2}% threshold." -f $linePercent, $MinimumLinePercent)
    }

    if ($branchPercent -lt $MinimumBranchPercent) {
        throw ("Branch coverage {0:N2}% is below the {1:N2}% threshold." -f $branchPercent, $MinimumBranchPercent)
    }
}

Write-Host ("Coverage gate passed: line >= {0:N2}%, branch >= {1:N2}%." -f $MinimumLinePercent, $MinimumBranchPercent)
