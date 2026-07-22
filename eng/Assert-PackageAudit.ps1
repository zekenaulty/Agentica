[CmdletBinding()]
param(
    [string] $Solution
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Solution)) {
    $Solution = Join-Path (Split-Path $PSScriptRoot -Parent) 'Agentica.slnx'
}

function Invoke-PackageAudit {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('vulnerable', 'deprecated')]
        [string] $Mode
    )

    $output = & dotnet list $Solution package "--$Mode" --include-transitive --no-restore --format json
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet package audit '$Mode' failed with exit code $LASTEXITCODE."
    }

    $document = ($output -join [Environment]::NewLine) | ConvertFrom-Json
    $findings = [System.Collections.Generic.List[object]]::new()

    foreach ($project in $document.projects) {
        if (-not ($project.PSObject.Properties.Name -contains 'frameworks')) {
            continue
        }

        foreach ($framework in $project.frameworks) {
            foreach ($scope in @('topLevelPackages', 'transitivePackages')) {
                if (-not ($framework.PSObject.Properties.Name -contains $scope)) {
                    continue
                }

                foreach ($package in $framework.$scope) {
                    $requestedVersion = if ($package.PSObject.Properties.Name -contains 'requestedVersion') {
                        $package.requestedVersion
                    }
                    else {
                        $null
                    }

                    $findings.Add([pscustomobject]@{
                        Project = $project.path
                        Framework = $framework.framework
                        Scope = $scope
                        Id = $package.id
                        Requested = $requestedVersion
                        Resolved = $package.resolvedVersion
                    })
                }
            }
        }
    }

    if ($findings.Count -gt 0) {
        $rendered = $findings | Format-Table -AutoSize | Out-String
        throw "Package audit '$Mode' found $($findings.Count) package(s):`n$rendered"
    }

    Write-Host "Package audit '$Mode': clean."
}

Invoke-PackageAudit -Mode vulnerable
Invoke-PackageAudit -Mode deprecated
