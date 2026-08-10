# Unit test suite for shape parsers
$PSScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Get-ChildItem "$PSScriptRoot\ConvertFrom-*.ps1" | ForEach-Object {
    $name = $_.BaseName
    $content = Get-Content $_.FullName -Raw
    Invoke-Expression "function global:$name { $content }"
}
# Helper assert function
function Assert-Equal($Actual, $Expected, $Msg) {
    if ($Actual -ne $Expected) {
        Write-Error "Assertion Failed: $Msg (Expected: '$Expected', Actual: '$Actual')"
        return $false
    }
    return $true
}

$allPass = $true

Write-Host "Running parser unit tests..." -ForegroundColor Cyan

# 1. Test ConvertFrom-KeyValue
Write-Host "Testing ConvertFrom-KeyValue..." -ForegroundColor Gray
$kvInput = @"
Level: 100
Technology: Li-ion
[sys.usb.config]: [adb]
[sys.usb.state]: [adb]
"@ -split "`r?`n"

$kvResult = $kvInput | ConvertFrom-KeyValue
$allPass = $allPass -and (Assert-Equal $kvResult.Level "100" "KV parser property Level")
$allPass = $allPass -and (Assert-Equal $kvResult.Technology "Li-ion" "KV parser property Technology")
$allPass = $allPass -and (Assert-Equal $kvResult.'sys.usb.config' "adb" "KV parser property sys.usb.config")

# 2. Test ConvertFrom-Settings
Write-Host "Testing ConvertFrom-Settings..." -ForegroundColor Gray
$settingsInput = @"
airplane_mode_on=0
device_name=motorola razr+ 2024
"@ -split "`r?`n"

$settingsResult = $settingsInput | ConvertFrom-Settings
$allPass = $allPass -and (Assert-Equal $settingsResult.Count 2 "Settings parsed count")
$allPass = $allPass -and (Assert-Equal $settingsResult[0].Key "airplane_mode_on" "Settings key 0")
$allPass = $allPass -and (Assert-Equal $settingsResult[0].Value "0" "Settings value 0")
$allPass = $allPass -and (Assert-Equal $settingsResult[1].Key "device_name" "Settings key 1")
$allPass = $allPass -and (Assert-Equal $settingsResult[1].Value "motorola razr+ 2024" "Settings value 1")

# 3. Test ConvertFrom-Table
Write-Host "Testing ConvertFrom-Table..." -ForegroundColor Gray
$tableInput = @"
PID PPID USER RSS_KB Name
123 1 root 4567 system_server
789 123 radio 1234 com.android.phone
"@ -split "`r?`n"

$tableResult = $tableInput | ConvertFrom-Table
$allPass = $allPass -and (Assert-Equal $tableResult.Count 2 "Table parsed count")
$allPass = $allPass -and (Assert-Equal $tableResult[0].PID "123" "Table PID 0")
$allPass = $allPass -and (Assert-Equal $tableResult[0].Name "system_server" "Table Name 0")
$allPass = $allPass -and (Assert-Equal $tableResult[1].PID "789" "Table PID 1")
$allPass = $allPass -and (Assert-Equal $tableResult[1].Name "com.android.phone" "Table Name 1")

# 4. Test ConvertFrom-DumpsysTree
Write-Host "Testing ConvertFrom-DumpsysTree..." -ForegroundColor Gray
$treeInput = @"
Settings:
  version=4
  min_futurity=+5s0ms
Whitelist system apps:
  com.android.providers.calendar
  com.motorola.mobiledesktop.core
"@ -split "`r?`n"

$treeResult = $treeInput | ConvertFrom-DumpsysTree
$allPass = $allPass -and (Assert-Equal $treeResult.Settings.version "4" "Tree nested object property version")
$allPass = $allPass -and (Assert-Equal $treeResult.Settings.min_futurity "+5s0ms" "Tree nested object property min_futurity")
$allPass = $allPass -and (Assert-Equal $treeResult.'Whitelist system apps'.Count 2 "Tree array child count")
$allPass = $allPass -and (Assert-Equal $treeResult.'Whitelist system apps'[0] "com.android.providers.calendar" "Tree array child 0")
$allPass = $allPass -and (Assert-Equal $treeResult.'Whitelist system apps'[1] "com.motorola.mobiledesktop.core" "Tree array child 1")

if ($allPass) {
    Write-Host "All parser unit tests PASSED!" -ForegroundColor Green
    exit 0
} else {
    Write-Host "Some unit tests FAILED!" -ForegroundColor Red
    exit 1
}
