# Remedy Build Environment Helper Script
# Usage: . .\setenv.ps1

$pathsToAdd = @(
    "S:\bin\msvc",
    "S:\bin\Windows Kits\10\bin\10.0.26100.0\x64",
    "S:\bin\platform-tools",
    "S:\bin\nuget"
)

$currentPaths = $env:PATH -split ';'
foreach ($p in $pathsToAdd) {
    if ($currentPaths -notcontains $p) {
        $env:PATH = "$p;$env:PATH"
    }
}

$env:INCLUDE = "S:\bin\msvc\include;S:\bin\Windows Kits\10\Include\10.0.26100.0\ucrt;S:\bin\Windows Kits\10\Include\10.0.26100.0\um;S:\bin\Windows Kits\10\Include\10.0.26100.0\shared;S:\bin\Windows Kits\10\Include\10.0.26100.0\winrt"
$env:LIB = "S:\bin\msvc\lib\x64;S:\bin\Windows Kits\10\Lib\10.0.26100.0\ucrt\x64;S:\bin\Windows Kits\10\Lib\10.0.26100.0\um\x64"

Write-Host "[Remedy Environment] MSVC, Windows Kits, platform-tools, and nuget paths configured." -ForegroundColor Green
