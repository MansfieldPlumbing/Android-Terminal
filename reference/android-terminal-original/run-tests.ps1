# Host-Side PowerShell Orchestrator for Android Subsystem Tests
# Requires ADB in PATH

$ErrorActionPreference = "Stop"

$AdbPath = "adb"

Write-Host "Verifying ADB connection..." -ForegroundColor Cyan
$devices = & $AdbPath devices
if ($devices -notmatch "device`r?`n") {
    Write-Host "Error: No Android device connected via ADB." -ForegroundColor Red
    exit 1
}

Write-Host "Pushing test script to device..." -ForegroundColor Cyan
& $AdbPath push .\test-verbs.ps1 /storage/emulated/0/test-verbs.ps1 | Out-Null

Write-Host "Setting up ADB port forward..." -ForegroundColor Cyan
& $AdbPath forward tcp:8080 tcp:8080

$ws = [System.Net.WebSockets.ClientWebSocket]::new()
$cts = [System.Threading.CancellationTokenSource]::new()
$uri = [System.Uri]::new("ws://localhost:8080/")

Write-Host "Connecting to Projection Server at ws://localhost:8080/..." -ForegroundColor Cyan
try {
    $connTask = $ws.ConnectAsync($uri, $cts.Token)
    while (-not $connTask.IsCompleted) { Start-Sleep -Milliseconds 100 }
    if ($connTask.IsFaulted) { throw $connTask.Exception }
} catch {
    Write-Host "Failed to connect to WebSocket. Is the Terminal app running and 'Cast to Desktop' active?" -ForegroundColor Red
    exit 1
}

Write-Host "Connected successfully. Spinning up dedicated test runspace (Tab 1337)..." -ForegroundColor Green

# 1. Create dedicated test session
$createPayload = '{"type":"createSession","tabId":1337}'
$createBytes = [System.Text.Encoding]::UTF8.GetBytes($createPayload)
$createTask = $ws.SendAsync([System.ArraySegment[byte]]::new($createBytes), [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $cts.Token)
while (-not $createTask.IsCompleted) { Start-Sleep -Milliseconds 5 }

$buffer = [byte[]]::new(16384)
$segment = [System.ArraySegment[byte]]::new($buffer)
$commandSent = $false

Write-Host "`n--- TERMINAL OUTPUT ---" -ForegroundColor DarkGray

while ($ws.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
    $receiveTask = $ws.ReceiveAsync($segment, $cts.Token)
    while (-not $receiveTask.IsCompleted) { Start-Sleep -Milliseconds 50 }
    
    if ($receiveTask.IsFaulted) { break }
    if ($receiveTask.Result.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) { break }
    
    $text = [System.Text.Encoding]::UTF8.GetString($buffer, 0, $receiveTask.Result.Count)
    $colonIdx = $text.IndexOf(':')
    
    if ($colonIdx -gt 0) {
        $msgTabId = $text.Substring(0, $colonIdx)
        $content = $text.Substring($colonIdx + 1)
        
        # Only process output from our dedicated test tab
        if ($msgTabId -eq "1337") {
            Write-Host -NoNewline $content
            
            # Send the test execution command once we receive the initial prompt
            if (-not $commandSent -and $content -like "*PS>*") {
                $commandSent = $true
                $cmdStr = ". /storage/emulated/0/test-verbs.ps1`r"
                
                foreach ($char in $cmdStr.ToCharArray()) {
                    $key = $char.ToString()
                    if ($char -eq "`r") { $key = "Enter" }
                    
                    $payloadObj = @{ type="input"; tabId=1337; key=$key }
                    $payloadStr = $payloadObj | ConvertTo-Json -Compress
                    $payloadBytes = [System.Text.Encoding]::UTF8.GetBytes($payloadStr)
                    
                    $sendTask = $ws.SendAsync([System.ArraySegment[byte]]::new($payloadBytes), [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $cts.Token)
                    while (-not $sendTask.IsCompleted) { Start-Sleep -Milliseconds 5 }
                    Start-Sleep -Milliseconds 15
                }
            }
            
            # Look for the completion string
            if ($content -like "*Verification Suite Completed Successfully!*") {
                Write-Host "`n--- TEST EXECUTION FINISHED ---" -ForegroundColor DarkGray
                break
            }
        }
    }
}

# Cleanup
try { $ws.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, "Done", $cts.Token).Wait() } catch {}
$ws.Dispose()
$cts.Dispose()

Write-Host "Pulling execution logs from device..." -ForegroundColor Cyan
& $AdbPath pull /storage/emulated/0/test-execution.log .\test-execution.log | Out-Null
Write-Host "Done! Execution log saved to: $(Join-Path (Get-Location) 'test-execution.log')" -ForegroundColor Green
