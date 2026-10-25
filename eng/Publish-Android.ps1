#Requires -Version 7.4
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [ValidateRange(1, 2100000000)][int]$ApplicationVersion,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$ApplicationDisplayVersion,
    [string]$OutputDirectory,
    [string]$Keystore,
    [string]$KeystorePassword,
    [string]$KeyAlias,
    [string]$KeyPassword
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path $PSScriptRoot -Parent
$projectPath = Join-Path $root 'src/DeveLiveStreamer.App/DeveLiveStreamer.App.csproj'
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
if (-not $Version) { $Version = if ($env:BUILD_VERSION) { $env:BUILD_VERSION } else { $project.Project.PropertyGroup.ApplicationDisplayVersion | Where-Object { $_ } | Select-Object -First 1 } }
if (-not $ApplicationVersion) { $ApplicationVersion = if ($env:BUILD_NUMBER) { [int]$env:BUILD_NUMBER } else { [int]($project.Project.PropertyGroup.ApplicationVersion | Where-Object { $_ } | Select-Object -First 1) } }
if (-not $ApplicationDisplayVersion) { $ApplicationDisplayVersion = $Version }
if (-not $OutputDirectory) { $OutputDirectory = 'artifacts/android' }
$output = [IO.Path]::GetFullPath($OutputDirectory, $root)
$temporaryKeystore = $null
$previousStorePassword = $env:DEVELIVESTREAMER_SIGNING_STORE_PASSWORD
$previousKeyPassword = $env:DEVELIVESTREAMER_SIGNING_KEY_PASSWORD
$previousVerificationPassword = $env:ANDROID_KEYSTORE_PASSWORD

try {
    if (-not $Keystore -and $env:ANDROID_KEYSTORE_BASE64) {
        $temporaryKeystore = [IO.Path]::GetTempFileName()
        if (-not $IsWindows) { & chmod 600 $temporaryKeystore }
        [IO.File]::WriteAllBytes($temporaryKeystore, [Convert]::FromBase64String($env:ANDROID_KEYSTORE_BASE64))
        $Keystore = $temporaryKeystore
    }
    if (-not $KeystorePassword) { $KeystorePassword = $env:ANDROID_KEYSTORE_PASSWORD }
    if (-not $KeyAlias) { $KeyAlias = $env:ANDROID_KEY_ALIAS }
    if (-not $KeyPassword) { $KeyPassword = $env:ANDROID_KEY_PASSWORD }
    $hasSigningConfiguration = $Keystore -or $KeystorePassword -or $KeyAlias -or $KeyPassword
    if ($hasSigningConfiguration) {
        if (-not ($Keystore -and $KeystorePassword -and $KeyAlias -and $KeyPassword)) {
            throw 'Release signing requires a keystore and all three credentials: ANDROID_KEYSTORE_PASSWORD, ANDROID_KEY_ALIAS, ANDROID_KEY_PASSWORD.'
        }
        $Keystore = [IO.Path]::GetFullPath($Keystore, $root)
        if (-not (Test-Path -LiteralPath $Keystore -PathType Leaf)) { throw "Keystore does not exist: $Keystore" }
        $signingLabel = 'release'
    }
    else {
        $Keystore = Join-Path $root '.tools/development.keystore'
        $KeystorePassword = 'android'
        $KeyAlias = 'androiddebugkey'
        $KeyPassword = 'android'
        $signingLabel = 'development'
        if (-not (Test-Path -LiteralPath $Keystore)) {
            New-Item -ItemType Directory -Force (Split-Path $Keystore -Parent) | Out-Null
            & keytool -genkeypair -keystore $Keystore -storepass android -alias $KeyAlias -keypass android `
                -dname 'CN=DeveLiveStreamer Development,O=Deve,C=NL' -keyalg RSA -keysize 2048 -validity 10000 -noprompt
        }
    }

    $env:DEVELIVESTREAMER_SIGNING_STORE_PASSWORD = $KeystorePassword
    $env:DEVELIVESTREAMER_SIGNING_KEY_PASSWORD = $KeyPassword
    $env:ANDROID_KEYSTORE_PASSWORD = $KeystorePassword
    # Validate credentials before removing old packages or running a long publish.
    & keytool -list -keystore $Keystore -alias $KeyAlias -storepass:env DEVELIVESTREAMER_SIGNING_STORE_PASSWORD | Out-Null
    New-Item -ItemType Directory -Force $output | Out-Null
    Get-ChildItem -LiteralPath $output -Filter '*.apk' -File | Remove-Item -Force
    # Android's incremental signing target does not track changes to the keystore.
    $bin = Join-Path $root "src/DeveLiveStreamer.App/bin/$Configuration"
    if (Test-Path -LiteralPath $bin) {
        Get-ChildItem -LiteralPath $bin -Filter '*-Signed.apk' -Recurse -File | Remove-Item -Force
    }
    $properties = @("-p:Version=$Version", "-p:ApplicationVersion=$ApplicationVersion", "-p:ApplicationDisplayVersion=$ApplicationDisplayVersion")
    if ($env:ANDROID_HOME) { $properties += "-p:AndroidSdkDirectory=$env:ANDROID_HOME" }
    if ($env:JAVA_HOME) { $properties += "-p:JavaSdkDirectory=$env:JAVA_HOME" }
    dotnet restore $projectPath --locked-mode @properties
    $arguments = @(
        'publish', $projectPath, '--framework', 'net10.0-android', '--configuration', $Configuration,
        '--no-restore', '-p:AndroidPackageFormats=apk',
        "-p:PublishDir=$output$([IO.Path]::DirectorySeparatorChar)", '-p:AndroidKeyStore=true',
        "-p:AndroidSigningKeyStore=$Keystore", '-p:AndroidSigningStorePass=env:DEVELIVESTREAMER_SIGNING_STORE_PASSWORD',
        "-p:AndroidSigningKeyAlias=$KeyAlias", '-p:AndroidSigningKeyPass=env:DEVELIVESTREAMER_SIGNING_KEY_PASSWORD'
    ) + $properties
    dotnet @arguments
    $packages = @(Get-ChildItem -LiteralPath $output -Filter '*-Signed.apk' -File)
    if ($packages.Count -ne 1) { throw "Expected one signed APK in $output, found $($packages.Count)." }
    $target = Join-Path $output "DeveLiveStreamer-$Version-$signingLabel.apk"
    Move-Item -LiteralPath $packages[0].FullName -Destination $target -Force
    Get-ChildItem -LiteralPath $output -Filter '*.apk' -File | Where-Object { $_.FullName -ne $target } | Remove-Item -Force
    $python = if ($IsWindows) { 'python' } else { 'python3' }
    & $python (Join-Path $PSScriptRoot 'verify-apk.py') $target $Keystore $KeyAlias $ApplicationVersion $ApplicationDisplayVersion
    Write-Host "Published and verified Android APK: $target"
}
finally {
    $env:DEVELIVESTREAMER_SIGNING_STORE_PASSWORD = $previousStorePassword
    $env:DEVELIVESTREAMER_SIGNING_KEY_PASSWORD = $previousKeyPassword
    $env:ANDROID_KEYSTORE_PASSWORD = $previousVerificationPassword
    if ($temporaryKeystore) { Remove-Item -LiteralPath $temporaryKeystore -Force -ErrorAction SilentlyContinue }
}
