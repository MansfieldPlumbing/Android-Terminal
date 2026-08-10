param(
    [string]$DotNet = 'S:\bin\dotnet\dotnet.exe',
    [string]$AndroidSdk = 'S:\bin\android-sdk',
    [string]$JavaSdk = 'S:\bin\jdk',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug'
)

$project = Join-Path $PSScriptRoot '..\src\Terminal.Android\Terminal.Android.csproj'
$packagedSettings = Join-Path $PSScriptRoot '..\src\Terminal.Android\Assets\settings.ps1'
$spareSettings = Join-Path $PSScriptRoot '..\templates\settings.default.ps1'
if ((Get-FileHash $packagedSettings).Hash -ne (Get-FileHash $spareSettings).Hash) {
    throw 'templates/settings.default.ps1 has drifted from the packaged Assets/settings.ps1.'
}
& $DotNet build $project -c $Configuration -r android-arm64 `
    -p:AndroidSdkDirectory=$AndroidSdk -p:JavaSdkDirectory=$JavaSdk
if ($LASTEXITCODE) { exit $LASTEXITCODE }

$output = Join-Path $PSScriptRoot "..\build\bin\Terminal.Android\$Configuration\net11.0-android\android-arm64"
Write-Host "Terminal build output: $([IO.Path]::GetFullPath($output))"
