[CmdletBinding()]
param(
    [switch]$Test
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot
$buildDir = Join-Path $repoRoot "build\router\windows"
$cmake = @(
    "C:\bin\cmake\bin\cmake.exe",
    "S:\bin\cmake\bin\cmake.exe"
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$ctest = if ($cmake) { Join-Path (Split-Path -Parent $cmake) "ctest.exe" } else { $null }
$dotnet = @(
    "C:\bin\pwsh\dotnet.exe",
    "S:\bin\dotnet\dotnet.exe"
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

foreach ($required in @($cmake, $dotnet, (Join-Path $repoRoot "deps\Remedy\CMakeLists.txt"))) {
    if (-not $required -or -not (Test-Path -LiteralPath $required)) {
        throw "Required router build dependency was not found: $required"
    }
}

& $cmake -S $repoRoot -B $buildDir "-DTERMINAL_DOTNET=$dotnet"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $cmake --build $buildDir --config Debug
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Test) {
    if (-not (Test-Path -LiteralPath $ctest)) {
        throw "CTest was not found beside CMake: $ctest"
    }
    & $ctest --test-dir $buildDir --output-on-failure -C Debug -R "^test_terminal_"
    exit $LASTEXITCODE
}

exit 0
