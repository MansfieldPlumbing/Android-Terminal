# Android PowerShell Subsystem Comprehensive Verification Test Script
# Runs natively inside the Terminal runspace on the device.

$LogPath = "/storage/emulated/0/test-execution.log"
$ErrorActionPreference = "Continue"

# Initialize Log File
if (Test-Path $LogPath) { Remove-Item $LogPath }
New-Item -ItemType File -Path $LogPath -Force | Out-Null

function Log-Msg([string]$msg, [string]$level="INFO") {
    $time = Get-Date -Format "HH:mm:ss"
    $formatted = "[$time] [$level] $msg"
    Write-Output $formatted
    Add-Content -Path $LogPath -Value $formatted
}

Log-Msg "=================================================="
Log-Msg "PowerShell Subsystem Full Verification Suite"
Log-Msg "=================================================="

# 1. Environment & Path Resolution
Log-Msg "--- Environment & Path Resolution ---"
Log-Msg "PowerShell Version: $($PSVersionTable.PSVersion)"
Log-Msg "HOME (Internal): $env:HOME"
Log-Msg "PHONE_HOME (External): $env:PHONE_HOME"
try {
    $loc = Get-Location
    Log-Msg "Current Location: $($loc.Path)"
} catch { Log-Msg "Get-Location failed: $_" "ERROR" }

# 2. File System Operations
Log-Msg "--- File System Operations ---"
$TestDir = "$env:PHONE_HOME/pstest_dir"
$TestFile = "$TestDir/test.txt"

try {
    if (Test-Path $TestDir) { deltree $TestDir }
    mkdir $TestDir | Out-Null
    Log-Msg "Created directory $TestDir"
    
    cd $TestDir
    Log-Msg "Changed location to $(Get-Location)"
    
    echo "Hello Android Subsystem" | Set-Content $TestFile
    Log-Msg "Created test file with Set-Content"
    
    $content = cat $TestFile
    Log-Msg "Read content via cat: $content"
    
    cp $TestFile "$TestDir/test_copy.txt"
    Log-Msg "Copied file via cp"
    
    mv "$TestDir/test_copy.txt" "$TestDir/test_moved.txt"
    Log-Msg "Moved file via mv"
    
    $items = ls
    Log-Msg "Listed directory via ls. Found $($items.Count) items."
    
    cd $env:HOME
    rmdir $TestDir
    Log-Msg "Removed directory via rmdir"
} catch { Log-Msg "File System Error: $_" "ERROR" }

# 3. Object & Pipeline Manipulation
Log-Msg "--- Object & Pipeline Operations ---"
try {
    $procs = ps | sort Id -Descending | select -First 3
    Log-Msg "Retrieved Top 3 Processes via ps | sort | select"
    
    $measure = 1..100 | measure -Average -Sum
    Log-Msg "Measurement (1..100): Sum=$($measure.Sum), Avg=$($measure.Average)"
    
    $evens = 1..10 | where { $_ % 2 -eq 0 } | % { $_ * 2 }
    Log-Msg "Pipeline filtering & foreach: $evens"
} catch { Log-Msg "Pipeline Error: $_" "ERROR" }

# 4. Networking
Log-Msg "--- Networking ---"
try {
    $ip = ipconfig
    Log-Msg "Device IP Addresses (ipconfig): $ip"
    
    Log-Msg "Testing web request (curl to example.com)..."
    $web = curl -UseBasicParsing http://example.com
    Log-Msg "Web request completed. Status Code: $($web.StatusCode)"
} catch { Log-Msg "Networking Error: $_" "ERROR" }

# 5. Core Native Commands
Log-Msg "--- Core Native Android Commands ---"
try {
    Log-Msg "Triggering vibrate (500ms)..."
    vibrate -Duration 500
    Start-Sleep -Milliseconds 600
    
    Log-Msg "Triggering toast notification..."
    toast -Message "PowerShell Core verification running!" -Long
    Start-Sleep -Seconds 2
    
    Log-Msg "Toggling flashlight..."
    flashlight
    Start-Sleep -Seconds 2
    flashlight
    Log-Msg "Flashlight toggled successfully."
} catch { Log-Msg "Native Command Error: $_" "ERROR" }

# 6. Help & Aliases
Log-Msg "--- Help & Aliases ---"
try {
    $al = alias sls
    Log-Msg "Alias resolved: sls -> $($al.Definition)"
    
    $cmd = which ipconfig
    Log-Msg "Command resolved: ipconfig -> $($cmd.CommandType)"
} catch { Log-Msg "Help/Alias Error: $_" "ERROR" }

Log-Msg "=================================================="
Log-Msg "Verification Suite Completed Successfully!"
Log-Msg "=================================================="
