[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts/packages'
}
$version = '0.1.0-research.1'
$packages = @(
    @{ Id = 'Agentica'; Project = 'Agentica/Agentica.csproj'; Dependency = $null },
    @{ Id = 'Agentica.Clients'; Project = 'Agentica.Clients/Agentica.Clients.csproj'; Dependency = 'Agentica' }
)

[xml] $labProject = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Agentica.Lab/Agentica.Lab.csproj') -Raw
if ($labProject.SelectSingleNode('/Project/PropertyGroup/IsPackable').'#text' -ne 'false' -or
    $labProject.SelectSingleNode('/Project/PropertyGroup/PackAsTool').'#text' -ne 'false') {
    throw 'Agentica.Lab must remain unpackable and must never be emitted as a .NET tool.'
}

[System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
foreach ($oldPackage in Get-ChildItem -LiteralPath $OutputDirectory -File | Where-Object {
    $_.Name -like 'Agentica*.nupkg' -or $_.Name -like 'Agentica*.snupkg'
}) {
    Remove-Item -LiteralPath $oldPackage.FullName -Force
}

foreach ($package in $packages) {
    $project = Join-Path $repositoryRoot $package.Project
    & dotnet pack $project --configuration $Configuration --no-build --no-restore --output $OutputDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Packing '$project' failed with exit code $LASTEXITCODE."
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Xml.Linq
foreach ($package in $packages) {
    $packagePath = Join-Path $OutputDirectory "$($package.Id).$version.nupkg"
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
        throw "Expected package '$packagePath' was not produced."
    }

    $symbolPackagePath = Join-Path $OutputDirectory "$($package.Id).$version.snupkg"
    if (-not (Test-Path -LiteralPath $symbolPackagePath -PathType Leaf)) {
        throw "Expected symbol package '$symbolPackagePath' was not produced."
    }

    $archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $entryNames = @($archive.Entries | ForEach-Object FullName)
        foreach ($requiredEntry in @('LICENSE', 'README.md', "lib/net10.0/$($package.Id).dll")) {
            if ($requiredEntry -notin $entryNames) {
                throw "Package '$($package.Id)' is missing '$requiredEntry'."
            }
        }

        if ($entryNames | Where-Object { $_ -match '(^|/)Agentica\.Lab(\.|/)' -or $_ -match '(^|/)\.env($|\.)' }) {
            throw "Package '$($package.Id)' contains Lab or environment content."
        }

        $nuspecEntry = $archive.Entries | Where-Object FullName -EQ "$($package.Id).nuspec" | Select-Object -First 1
        if ($null -eq $nuspecEntry) {
            throw "Package '$($package.Id)' has no expected nuspec."
        }

        $stream = $nuspecEntry.Open()
        try {
            $nuspec = [System.Xml.Linq.XDocument]::Load($stream)
        }
        finally {
            $stream.Dispose()
        }

        $metadata = $nuspec.Root.Elements() | Where-Object Name -Like '*metadata' | Select-Object -First 1
        $value = {
            param([string] $Name)
            ($metadata.Elements() | Where-Object { $_.Name.LocalName -eq $Name } | Select-Object -First 1).Value
        }

        if ((& $value 'id') -ne $package.Id -or (& $value 'version') -ne $version) {
            throw "Package '$($package.Id)' identity or version metadata is wrong."
        }

        $license = $metadata.Elements() | Where-Object { $_.Name.LocalName -eq 'license' } | Select-Object -First 1
        $repository = $metadata.Elements() | Where-Object { $_.Name.LocalName -eq 'repository' } | Select-Object -First 1
        if ($license.Attribute('type').Value -ne 'file' -or $license.Value -ne 'LICENSE') {
            throw "Package '$($package.Id)' does not carry the source-available license file metadata."
        }

        if ((& $value 'readme') -ne 'README.md' -or
            (& $value 'title') -notmatch 'Research Preview' -or
            (& $value 'description') -notmatch 'Internal source-available research preview' -or
            $repository.Attribute('type').Value -ne 'git' -or
            $repository.Attribute('url').Value -ne 'https://github.com/zekenaulty/Agentica.git') {
            throw "Package '$($package.Id)' is missing readme or source repository metadata."
        }

        if ($null -ne $package.Dependency) {
            $dependency = $metadata.Descendants() | Where-Object {
                $_.Name.LocalName -eq 'dependency' -and $_.Attribute('id').Value -eq $package.Dependency
            } | Select-Object -First 1
            if ($null -eq $dependency -or $dependency.Attribute('version').Value -notmatch [regex]::Escape($version)) {
                throw "Package '$($package.Id)' is not pinned to the matching Agentica research preview."
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

$consumerRoot = Join-Path ([System.IO.Path]::GetTempPath()) "agentica-package-consumer-$([Guid]::NewGuid().ToString('N'))"
[System.IO.Directory]::CreateDirectory($consumerRoot) | Out-Null
try {
    $escapedSource = [System.Security.SecurityElement]::Escape([System.IO.Path]::GetFullPath($OutputDirectory))
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RestoreSources>$escapedSource;https://api.nuget.org/v3/index.json</RestoreSources>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Agentica.Clients" Version="$version" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $consumerRoot 'Consumer.csproj') -Encoding UTF8

    @"
using Agentica;
using Agentica.Clients.Gemini;

var id = AgenticaIds.New("consumer");
if (!id.StartsWith("consumer_", StringComparison.Ordinal) || id.Length != 41)
{
    throw new InvalidOperationException("The packaged runtime did not produce the expected durable identifier shape.");
}

if (string.IsNullOrWhiteSpace(GeminiModelId.Flash25))
{
    throw new InvalidOperationException("The packaged provider client surface is unavailable.");
}

Console.WriteLine("agentica-consumer-ok");
"@ | Set-Content -LiteralPath (Join-Path $consumerRoot 'Program.cs') -Encoding UTF8

    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'global.json') -Destination $consumerRoot

    & dotnet restore (Join-Path $consumerRoot 'Consumer.csproj') --no-cache
    if ($LASTEXITCODE -ne 0) {
        throw "External package consumer restore failed with exit code $LASTEXITCODE."
    }

    & dotnet build (Join-Path $consumerRoot 'Consumer.csproj') --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "External package consumer build failed with exit code $LASTEXITCODE."
    }

    $consumerOutput = & dotnet run --project (Join-Path $consumerRoot 'Consumer.csproj') --configuration Release --no-build --no-restore
    if ($LASTEXITCODE -ne 0 -or $consumerOutput -notcontains 'agentica-consumer-ok') {
        throw "External package consumer run failed or did not produce its success sentinel."
    }
}
finally {
    if (Test-Path -LiteralPath $consumerRoot) {
        Remove-Item -LiteralPath $consumerRoot -Recurse -Force
    }
}

Write-Host "Package validation and external consumer smoke test passed for $version."
