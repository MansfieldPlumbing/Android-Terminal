[CmdletBinding()]
param(
    [string]$DeviceSerial = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot
$ndkRoot = "S:\bin\android-sdk\ndk\27.3.13750724"
$androidSdk = "S:\bin\android-sdk"
$javaSdk = "S:\bin\jdk"
$cmake = "S:\bin\cmake\bin\cmake.exe"
$ninja = "S:\bin\ninja\ninja.exe"
$adb = "S:\bin\platform-tools\adb.exe"
$dotnet = "S:\bin\dotnet\dotnet.exe"
$buildDir = Join-Path $repoRoot "build\router\android-cmake"
$routerBuildDir = Join-Path $repoRoot "build\router\android-arm64"
$llvmBin = Join-Path $ndkRoot "toolchains\llvm\prebuilt\windows-x86_64\bin"
$clang = Join-Path $llvmBin "aarch64-linux-android35-clang.cmd"
$routerProject = Join-Path $repoRoot "src\Terminal.Router\Terminal.Router.csproj"
$routerNativeSource = Join-Path $repoRoot "src\Terminal.Router\native\android\terminal_router_pty.c"
$routerNativeLibrary = Join-Path $routerBuildDir "libterminal_router_pty.so"
$routerExecutable = Join-Path $routerBuildDir "terminal-router"
$routerAotLibrary = Join-Path $routerBuildDir "libterminal-router.so"
$routerAotSource = Join-Path $repoRoot "build\bin\Terminal.Router\Release\net11.0-android\android-arm64\native\libterminal-router.so"

$requiredPaths = @(
    $cmake,
    $ninja,
    $adb,
    $dotnet,
    $clang,
    (Join-Path $javaSdk "bin\java.exe"),
    $routerProject,
    $routerNativeSource,
    (Join-Path $repoRoot "deps\Remedy\CMakeLists.txt"),
    (Join-Path $ndkRoot "build\cmake\android.toolchain.cmake")
)
foreach ($required in $requiredPaths) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required Android router dependency was not found: $required"
    }
}

New-Item -ItemType Directory -Force -Path $routerBuildDir | Out-Null

& $clang -shared -fPIC -O2 -Wall -Wextra -Werror $routerNativeSource -o $routerNativeLibrary
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $clang -DTERMINAL_ROUTER_LAUNCHER -O2 -Wall -Wextra -Werror $routerNativeSource -ldl -o $routerExecutable
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$savedNdkRoot = $env:ANDROID_NDK_ROOT
$savedNdkHome = $env:ANDROID_NDK_HOME
$savedPath = $env:PATH
try {
    $env:ANDROID_NDK_ROOT = $ndkRoot
    $env:ANDROID_NDK_HOME = $ndkRoot
    $env:PATH = "$llvmBin;$env:PATH"
    $dotnetArgs = @(
        "build",
        $routerProject,
        "-c", "Release",
        "-r", "android-arm64",
        "-p:AndroidSdkDirectory=$androidSdk",
        "-p:AndroidNdkDirectory=$ndkRoot",
        "-p:JavaSdkDirectory=$javaSdk",
        "-v:minimal"
    )
    & $dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    $env:ANDROID_NDK_ROOT = $savedNdkRoot
    $env:ANDROID_NDK_HOME = $savedNdkHome
    $env:PATH = $savedPath
}

if (-not (Test-Path -LiteralPath $routerAotSource)) {
    throw "NativeAOT router library was not produced: $routerAotSource"
}
Copy-Item -LiteralPath $routerAotSource -Destination $routerAotLibrary -Force

$cmakeArgs = @(
    "-S", $repoRoot,
    "-B", $buildDir,
    "-G", "Ninja",
    "-DCMAKE_MAKE_PROGRAM=$ninja",
    "-DCMAKE_TOOLCHAIN_FILE=$(Join-Path $ndkRoot 'build\cmake\android.toolchain.cmake')",
    "-DANDROID_ABI=arm64-v8a",
    "-DANDROID_PLATFORM=android-35",
    "-DANDROID_STL=c++_static",
    "-DCMAKE_BUILD_TYPE=Release"
)
& $cmake @cmakeArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $cmake --build $buildDir --target test_terminal_pty_router_android --parallel
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$adbArgs = @()
if (-not [string]::IsNullOrWhiteSpace($DeviceSerial)) {
    $adbArgs += @("-s", $DeviceSerial)
}
$remoteRoot = "/data/local/tmp/terminal-router-receipt"
& $adb @adbArgs shell "mkdir -p $remoteRoot"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$artifacts = @(
    @($routerExecutable, "$remoteRoot/terminal-router"),
    @($routerAotLibrary, "$remoteRoot/libterminal-router.so"),
    @($routerNativeLibrary, "$remoteRoot/libterminal_router_pty.so"),
    @((Join-Path $buildDir "test_terminal_pty_router_android"), "$remoteRoot/receipt")
)
foreach ($artifact in $artifacts) {
    & $adb @adbArgs push $artifact[0] $artifact[1]
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

& $adb @adbArgs shell "chmod 700 $remoteRoot/terminal-router $remoteRoot/receipt && chmod 600 $remoteRoot/libterminal-router.so $remoteRoot/libterminal_router_pty.so && LD_LIBRARY_PATH=$remoteRoot $remoteRoot/receipt $remoteRoot/terminal-router /system/bin/sh"
exit $LASTEXITCODE
