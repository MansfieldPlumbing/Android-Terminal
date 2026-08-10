param(
    [string]$DotNet = 'S:\bin\dotnet\dotnet.exe',
    [string]$PowerShell = 'C:\bin\pwsh\pwsh.exe',
    [string]$AndroidSdk = 'S:\bin\android-sdk',
    [string]$JavaSdk = 'S:\bin\jdk',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [string]$Recipe = (Join-Path $PSScriptRoot '..\releases\dev.xml'),
    [string]$BaseIdentity = 'terminal',
    [string]$SigningProfile = 'debug',
    [string]$Keystore,
    [switch]$SkipHydration
)

$project = Join-Path $PSScriptRoot '..\src\Terminal.Android\Terminal.Android.csproj'
$engineTests = Join-Path $PSScriptRoot '..\tests\Terminal.Engine.Tests\Terminal.Engine.Tests.csproj'
$packagedSettings = Join-Path $PSScriptRoot '..\src\Terminal.Android\Assets\settings.ps1'
$spareSettings = Join-Path $PSScriptRoot '..\templates\settings.default.ps1'
if ((Get-FileHash $packagedSettings).Hash -ne (Get-FileHash $spareSettings).Hash) {
    throw 'templates/settings.default.ps1 has drifted from the packaged Assets/settings.ps1.'
}
& $DotNet run --project $engineTests -c $Configuration
if ($LASTEXITCODE) { exit $LASTEXITCODE }
& $DotNet build $project -c $Configuration -r android-arm64 `
    -p:AndroidSdkDirectory=$AndroidSdk -p:JavaSdkDirectory=$JavaSdk
if ($LASTEXITCODE) { exit $LASTEXITCODE }

$output = Join-Path $PSScriptRoot "..\build\bin\Terminal.Android\$Configuration\net11.0-android\android-arm64"
Write-Host "Terminal build output: $([IO.Path]::GetFullPath($output))"

if (-not $SkipHydration) {
    $baseApk = Join-Path $output 'dev.mansfieldplumbing.terminal.apk'
    $publisher = Join-Path $PSScriptRoot 'Publish-TerminalRelease.ps1'
    $publish = @(
        '-NoLogo', '-NoProfile', '-File', $publisher,
        '-Recipe', $Recipe,
        '-BaseApk', $baseApk,
        '-BaseIdentity', $BaseIdentity,
        '-SigningProfile', $SigningProfile,
        '-BuildTools', (Join-Path $AndroidSdk 'build-tools\36.0.0'),
        '-JavaSdk', $JavaSdk
    )
    if (-not [string]::IsNullOrWhiteSpace($Keystore)) { $publish += @('-Keystore', $Keystore) }
    & $PowerShell @publish
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
}
