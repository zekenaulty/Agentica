[CmdletBinding()]
param(
    [string] $DotnetPath = 'dotnet',
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $GoogleConsumerVersion = '1.13.0'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$requiredSdk = (Get-Content -LiteralPath (Join-Path $repositoryRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
$version = '0.1.0-research.1'
$runRoot = Join-Path $repositoryRoot "artifacts/provider-consumer/$([Guid]::NewGuid().ToString('N'))"
$feed = Join-Path $runRoot 'feed'
$consumer = Join-Path $runRoot 'consumer'
$packageCache = Join-Path $runRoot 'packages'
$sourceRoot = Join-Path $PSScriptRoot 'provider-consumer'
$previousCliHome = $env:DOTNET_CLI_HOME
$previousCertificateGeneration = $env:DOTNET_GENERATE_ASPNET_CERTIFICATE
$previousTelemetryOptOut = $env:DOTNET_CLI_TELEMETRY_OPTOUT
$previousConsumerDotnet = $env:AGENTICA_PROVIDER_CONSUMER_DOTNET
$env:DOTNET_CLI_HOME = Join-Path $runRoot 'cli-home'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

Push-Location $repositoryRoot
try {
    [System.IO.Directory]::CreateDirectory($env:DOTNET_CLI_HOME) | Out-Null
    $actualSdk = (& $DotnetPath --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualSdk -ne $requiredSdk) {
        throw "Provider consumer requires the exact repository SDK $requiredSdk; observed '$actualSdk'."
    }
    foreach ($directory in @($feed, $consumer, $packageCache)) {
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    }

    & $DotnetPath restore (Join-Path $repositoryRoot 'Agentica.Clients/Agentica.Clients.csproj') --locked-mode --configfile (Join-Path $repositoryRoot 'NuGet.config')
    if ($LASTEXITCODE -ne 0) { throw 'Provider package locked restore failed.' }
    & $DotnetPath build (Join-Path $repositoryRoot 'Agentica.Clients/Agentica.Clients.csproj') --configuration $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Provider package build failed.' }
    foreach ($project in @('Agentica/Agentica.csproj', 'Agentica.Clients/Agentica.Clients.csproj')) {
        & $DotnetPath pack (Join-Path $repositoryRoot $project) --configuration $Configuration --no-build --no-restore --output $feed
        if ($LASTEXITCODE -ne 0) { throw "Packing '$project' failed." }
    }

    # Copy a standalone consumer: no project reference and no shared mutable output/cache.
    foreach ($file in @('ProviderConsumer.csproj', 'Program.cs', 'EmbeddingAttempt.cs',
        'BridgeContractFixtures.cs', 'RecoveryHost.cs', 'RecoveryTransport.cs', 'RecoveryCases.cs',
        'EmbeddingRecoveryCases.cs')) {
        Copy-Item -LiteralPath (Join-Path $sourceRoot $file) -Destination $consumer
    }
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'global.json') -Destination $consumer
    $escapedFeed = [System.Security.SecurityElement]::Escape($feed)
    # A read-only local feed can seed third-party packages without permitting a stale
    # same-version Agentica package from the global cache to replace this run's pack.
    $globalPackagePath = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }
    $cachedSource = ''
    $cachedMapping = ''
    if (Test-Path -LiteralPath $globalPackagePath -PathType Container) {
        $escapedCache = [System.Security.SecurityElement]::Escape($globalPackagePath)
        $cachedSource = "<add key=`"third-party-cache`" value=`"$escapedCache`" />"
        $cachedMapping = '<packageSource key="third-party-cache"><package pattern="*" /></packageSource>'
    }
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="packed-agentica" value="$escapedFeed" />
    $cachedSource
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="packed-agentica"><package pattern="Agentica" /><package pattern="Agentica.Clients" /></packageSource>
    $cachedMapping
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath (Join-Path $consumer 'NuGet.config') -Encoding utf8

    $projectPath = Join-Path $consumer 'ProviderConsumer.csproj'
    & $DotnetPath restore $projectPath --configfile (Join-Path $consumer 'NuGet.config') --packages $packageCache "-p:GoogleConsumerVersion=$GoogleConsumerVersion" "-p:AgenticaPackageVersion=$version"
    if ($LASTEXITCODE -ne 0) { throw 'Packed provider consumer restore failed.' }
    & $DotnetPath build $projectPath --configuration $Configuration --no-restore "-p:GoogleConsumerVersion=$GoogleConsumerVersion" "-p:AgenticaPackageVersion=$version"
    if ($LASTEXITCODE -ne 0) { throw 'Packed provider consumer build failed.' }
    $env:AGENTICA_PROVIDER_CONSUMER_DOTNET = (Get-Command $DotnetPath).Source
    $output = & $DotnetPath run --project $projectPath --configuration $Configuration --no-build --no-restore
    $runCode = $LASTEXITCODE
    $output | Tee-Object -FilePath (Join-Path $runRoot 'provider-consumer-results.log')
    if ($runCode -ne 0 -or $output -notcontains 'agentica-provider-consumer-a5-ok' -or
        $output -notcontains 'agentica-provider-consumer-p08-ok') {
        throw 'Packed provider consumer did not complete A5 and P08 recovery scenarios.'
    }

    # Prove the package cache contains these just-produced bytes, not an older
    # globally installed artifact with the same research-preview version.
    foreach ($id in @('Agentica', 'Agentica.Clients')) {
        $filename = "$id.$version.nupkg"
        $packedHash = (Get-FileHash -LiteralPath (Join-Path $feed $filename) -Algorithm SHA256).Hash
        $restoredPath = Join-Path $packageCache "$($id.ToLowerInvariant())/$version/$($filename.ToLowerInvariant())"
        $restoredHash = (Get-FileHash -LiteralPath $restoredPath -Algorithm SHA256).Hash
        if ($packedHash -ne $restoredHash) { throw "Consumer restored different '$id' package bytes." }
    }
    Write-Host "A5 and P08 packed consumer passed. Retained local evidence: $runRoot"
    Write-Host 'Offline synthetic host qualification only; live providers and installed Bridge remain NOT-RUN.'
}
finally {
    $env:DOTNET_CLI_HOME = $previousCliHome
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = $previousCertificateGeneration
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $previousTelemetryOptOut
    $env:AGENTICA_PROVIDER_CONSUMER_DOTNET = $previousConsumerDotnet
    Pop-Location
}
