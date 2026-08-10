[CmdletBinding()]
param(
    [switch]$Clean,
    [switch]$Test
)

$ErrorActionPreference = "Stop"

# Derive repository root dynamically from script location
$RepoRoot = $PSScriptRoot
$BuildDir = Join-Path $RepoRoot "build"

# Record original process environment values for restoration / comparison
$origPath = $env:PATH
$origInclude = $env:INCLUDE
$origLib = $env:LIB

# Evaluate toolchain observations
$hasCmake   = [bool](Get-Command "cmake.exe" -ErrorAction SilentlyContinue)
$hasCTest   = [bool](Get-Command "ctest.exe" -ErrorAction SilentlyContinue)
$hasCl      = [bool](Get-Command "cl.exe" -ErrorAction SilentlyContinue)
$hasInclude = -not [string]::IsNullOrWhiteSpace($env:INCLUDE)
$hasLib     = -not [string]::IsNullOrWhiteSpace($env:LIB)

$mode = $null

if ($hasCmake -and $hasCTest -and $hasCl -and $hasInclude -and $hasLib) {
    $mode = "A"
    Write-Host "[build.ps1] Mode A: Complete existing developer environment detected. Preserving process environment."
}
elseif ((-not $hasCl) -and (-not $hasInclude) -and (-not $hasLib)) {
    $mode = "B"
    Write-Host "[build.ps1] Mode B: Absent MSVC developer environment detected. Activating local S:\bin toolchain fallback."
}
else {
    [Console]::Error.WriteLine("[build.ps1] ERROR: Partially initialized or conflicting MSVC developer environment detected.`nObservations:`n  cmake: $hasCmake`n  ctest: $hasCTest`n  cl: $hasCl`n  INCLUDE: $hasInclude`n  LIB: $hasLib`nAn MSVC developer environment must be fully initialized (cl, INCLUDE, LIB, cmake, ctest) or completely uninitialized to use the local S:\bin fallback.")
    exit 1
}

$exitCode = 0
$failureMessage = $null

try {
    do {
        if ($mode -eq "B") {
            # Required fallback directories to verify before environment mutation
            $requiredFallbackDirs = @(
                "S:\bin\msvc\bin\Hostx64\x64",
                "S:\bin\msvc\include",
                "S:\bin\msvc\lib\x64",
                "S:\bin\Windows Kits\10\Include\10.0.26100.0\ucrt",
                "S:\bin\Windows Kits\10\Include\10.0.26100.0\um",
                "S:\bin\Windows Kits\10\Include\10.0.26100.0\shared",
                "S:\bin\Windows Kits\10\Lib\10.0.26100.0\ucrt\x64",
                "S:\bin\Windows Kits\10\Lib\10.0.26100.0\um\x64",
                "S:\bin\Windows Kits\10\bin\10.0.26100.0\x64"
            )

            foreach ($dir in $requiredFallbackDirs) {
                if (-not (Test-Path $dir)) {
                    $exitCode = 1
                    $failureMessage = "[build.ps1] Required fallback directory '$dir' does not exist."
                    break
                }
            }
            if ($exitCode -ne 0) { break }

            # Candidate fallback binary paths
            $fallbackBinPaths = @(
                "C:\bin\cmake\bin",
                "S:\bin\cmake\bin",
                "S:\bin\msvc\bin\Hostx64\x64",
                "S:\bin\Windows Kits\10\bin\10.0.26100.0\x64"
            )

            $currentPaths = $env:PATH -split ';' | Where-Object { $_ -ne "" }
            foreach ($p in $fallbackBinPaths) {
                if ((Test-Path $p) -and ($currentPaths -notcontains $p)) {
                    $env:PATH = "$p;$env:PATH"
                    $currentPaths = $env:PATH -split ';' | Where-Object { $_ -ne "" }
                }
            }

            # Set process-local INCLUDE and LIB constructed entirely from approved S:\bin trees
            $env:INCLUDE = "S:\bin\msvc\include;S:\bin\Windows Kits\10\Include\10.0.26100.0\ucrt;S:\bin\Windows Kits\10\Include\10.0.26100.0\um;S:\bin\Windows Kits\10\Include\10.0.26100.0\shared"
            $env:LIB = "S:\bin\msvc\lib\x64;S:\bin\Windows Kits\10\Lib\10.0.26100.0\ucrt\x64;S:\bin\Windows Kits\10\Lib\10.0.26100.0\um\x64"

            # Verify fallback coherence: cl.exe, link.exe, nmake.exe must resolve from S:\bin\msvc\bin\Hostx64\x64
            $clCmd = Get-Command "cl.exe" -ErrorAction SilentlyContinue
            $linkCmd = Get-Command "link.exe" -ErrorAction SilentlyContinue
            $nmakeCmd = Get-Command "nmake.exe" -ErrorAction SilentlyContinue
            $cmakeCmd = Get-Command "cmake.exe" -ErrorAction SilentlyContinue
            $ctestCmd = Get-Command "ctest.exe" -ErrorAction SilentlyContinue

            if (-not $clCmd -or -not $linkCmd -or -not $nmakeCmd -or -not $cmakeCmd -or -not $ctestCmd) {
                $exitCode = 1
                $failureMessage = "[build.ps1] Fallback toolchain verification failed: missing required executable(s)."
                break
            }

            $expectedMsvcBin = "S:\bin\msvc\bin\Hostx64\x64"
            if (-not ($clCmd.Source -like "$expectedMsvcBin*") -or
                -not ($linkCmd.Source -like "$expectedMsvcBin*") -or
                -not ($nmakeCmd.Source -like "$expectedMsvcBin*")) {
                $exitCode = 1
                $failureMessage = "[build.ps1] Fallback toolchain coherence check failed: cl.exe, link.exe, and nmake.exe must resolve from $expectedMsvcBin"
                break
            }
        }

        # Post-activation tool verification for both modes
        foreach ($tool in @("cmake", "ctest", "cl")) {
            if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
                $exitCode = 1
                $failureMessage = "[build.ps1] Required tool '$tool' could not be located in PATH."
                break
            }
        }
        if ($exitCode -ne 0) { break }

        if ([string]::IsNullOrWhiteSpace($env:INCLUDE) -or [string]::IsNullOrWhiteSpace($env:LIB)) {
            $exitCode = 1
            $failureMessage = "[build.ps1] Required environment variable INCLUDE or LIB is empty."
            break
        }

        if ($Clean) {
            Write-Host "[build.ps1] Cleaning build directory: $BuildDir"
            if (Test-Path $BuildDir) {
                Remove-Item -Recurse -Force $BuildDir
            }
        }

        if (-not (Test-Path $BuildDir)) {
            New-Item -ItemType Directory -Force -Path $BuildDir | Out-Null
        }

        Write-Host "[build.ps1] Configuring CMake..."
        & cmake.exe -S $RepoRoot -B $BuildDir
        if ($LASTEXITCODE -ne 0) {
            $exitCode = $LASTEXITCODE
            $failureMessage = "[build.ps1] CMake configuration failed with exit code $exitCode"
            break
        }

        Write-Host "[build.ps1] Building CMake targets..."
        & cmake.exe --build $BuildDir --config Debug
        if ($LASTEXITCODE -ne 0) {
            $exitCode = $LASTEXITCODE
            $failureMessage = "[build.ps1] CMake build failed with exit code $exitCode"
            break
        }

        if ($Test) {
            Write-Host "[build.ps1] Running CTest..."
            & ctest.exe --test-dir $BuildDir --output-on-failure -C Debug
            if ($LASTEXITCODE -ne 0) {
                $exitCode = $LASTEXITCODE
                $failureMessage = "[build.ps1] CTest failed with exit code $exitCode"
                break
            }
        }
    } while ($false)
}
finally {
    if ($mode -eq "B") {
        $env:PATH = $origPath
        $env:INCLUDE = $origInclude
        $env:LIB = $origLib
    }
}

if ($exitCode -ne 0) {
    [Console]::Error.WriteLine($failureMessage)
    exit $exitCode
}

Write-Host "[build.ps1] Build driver completed successfully."