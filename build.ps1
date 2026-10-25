#Requires -Version 7.4
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [ValidateRange(1, 2100000000)][int]$ApplicationVersion,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$ApplicationDisplayVersion,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = $PSScriptRoot
$installedWorkloads = dotnet workload list | Out-String
if ($installedWorkloads -notmatch '(?m)^\s*maui-android\s') {
    throw 'Install the Android workload once with: dotnet workload install maui-android --skip-manifest-update'
}

[xml]$project = Get-Content -LiteralPath (Join-Path $root 'src/DeveLiveStreamer.App/DeveLiveStreamer.App.csproj') -Raw
if (-not $Version) { $Version = if ($env:BUILD_VERSION) { $env:BUILD_VERSION } else { $project.Project.PropertyGroup.ApplicationDisplayVersion | Where-Object { $_ } | Select-Object -First 1 } }
if (-not $ApplicationVersion) { $ApplicationVersion = if ($env:BUILD_NUMBER) { [int]$env:BUILD_NUMBER } else { [int]($project.Project.PropertyGroup.ApplicationVersion | Where-Object { $_ } | Select-Object -First 1) } }
if (-not $ApplicationDisplayVersion) { $ApplicationDisplayVersion = $Version }
$properties = @("-p:Version=$Version", "-p:ApplicationVersion=$ApplicationVersion", "-p:ApplicationDisplayVersion=$ApplicationDisplayVersion")
$solution = Join-Path $root 'DeveLiveStreamer.slnx'

Push-Location $root
try {
    dotnet restore $solution --locked-mode
    dotnet build $solution --configuration $Configuration --no-restore @properties
    foreach ($name in @('Core', 'Streaming')) {
        dotnet test "tests/DeveLiveStreamer.$name.Tests/DeveLiveStreamer.$name.Tests.csproj" `
            --configuration $Configuration --no-build --no-restore --collect 'XPlat Code Coverage' `
            --logger trx --results-directory (Join-Path $root 'artifacts/test-results')
    }
    if (-not $SkipPublish) {
        & (Join-Path $root 'eng/Publish-Android.ps1') -Configuration $Configuration -Version $Version `
            -ApplicationVersion $ApplicationVersion -ApplicationDisplayVersion $ApplicationDisplayVersion
    }
}
finally {
    Pop-Location
}
