<#
.SYNOPSIS
  Subsystem CLI — a PowerShell client for a Subsystem instance (Android phone running the app).

  The phone exposes the entire system as a JSON/WebSocket API on :8080. This module is a thin
  client over that contract, so any device running the app is controllable "for free". The app
  serves this very module at GET /cli, so onboarding is:

      adb forward tcp:8080 tcp:8080
      irm http://127.0.0.1:8080/cli | Out-File Subsystem.psm1 ; Import-Module ./Subsystem.psm1
      Connect-Subsystem
      Invoke-Subsystem 'Get-AndroidBattery'

  Cmdlets:
    Connect-Subsystem        resolve transport + health-check, sets the active base URL
    Invoke-Subsystem         run any device PWSH command, get parsed JSON back   (POST /api/exec)
    Invoke-SubsystemObject   run a command, get LIVE PSObjects back (CLIXML)     (POST /clixml)
    Invoke-SubsystemAgent    talk to the on-device model; -Raw shows the exact frames the WebView gets
    Get-SubsystemModel       list the model catalog + install state              (WS /models)
    Install-SubsystemModel   download a model (streams progress)                 (WS /models)
    Remove-SubsystemModel    delete a model from device storage                  (WS /models)
    Measure-SubsystemModel   profile the model (init, TTFT, prefill/decode tok/s) (WS /agent)
#>

$script:SubsystemBaseUrl = $null

function script:WsUrl([string]$path) {
    $u = [Uri]$script:SubsystemBaseUrl
    "ws://$($u.Host):$($u.Port)$path"
}

# Minimal request/response over a one-shot WebSocket: opens, optionally sends a frame, then yields
# every JSON frame received (as PSObjects) until $until returns $true or the socket closes.
function script:InvokeWsFrames {
    param(
        [string]$Path,
        [object]$Send,                 # hashtable to JSON-send after connect, or $null
        [scriptblock]$OnFrame,         # called with each parsed frame object
        [scriptblock]$Until            # return $true to stop (given the frame)
    )
    if (-not $script:SubsystemBaseUrl) { throw "Not connected. Run Connect-Subsystem first." }
    $ws = [System.Net.WebSockets.ClientWebSocket]::new()
    $cts = [System.Threading.CancellationTokenSource]::new()
    try {
        [void]$ws.ConnectAsync([Uri](WsUrl $Path), $cts.Token).GetAwaiter().GetResult()
        if ($Send) {
            $json = ($Send | ConvertTo-Json -Compress -Depth 6)
            $bytes = [Text.Encoding]::UTF8.GetBytes($json)
            [void]$ws.SendAsync([ArraySegment[byte]]::new($bytes), 'Text', $true, $cts.Token).GetAwaiter().GetResult()
        }
        $buf = [byte[]]::new(16384)
        $sb = [Text.StringBuilder]::new()
        while ($ws.State -eq 'Open') {
            $sb.Clear() | Out-Null
            do {
                $res = $ws.ReceiveAsync([ArraySegment[byte]]::new($buf), $cts.Token).GetAwaiter().GetResult()
                if ($res.MessageType -eq 'Close') { break }
                $sb.Append([Text.Encoding]::UTF8.GetString($buf, 0, $res.Count)) | Out-Null
            } while (-not $res.EndOfMessage)
            $txt = $sb.ToString()
            if ([string]::IsNullOrWhiteSpace($txt)) { continue }
            $frame = $null
            try { $frame = $txt | ConvertFrom-Json } catch { continue }
            if ($OnFrame) { & $OnFrame $frame }
            if ($Until -and (& $Until $frame)) { break }
        }
    }
    finally {
        try { [void]$ws.CloseAsync('NormalClosure', '', $cts.Token).GetAwaiter().GetResult() } catch {}
        $ws.Dispose(); $cts.Dispose()
    }
}

function Connect-Subsystem {
    [CmdletBinding()]
    param(
        [string]$BaseUrl,                          # e.g. http://192.168.1.50:8080
        [switch]$AdbForward,                        # set up adb forward tcp:8080 and use localhost
        [string]$Serial,                            # specific adb device serial
        [int]$Port = 8080
    )
    if ($AdbForward -or (-not $BaseUrl)) {
        if ($AdbForward) {
            $adbArgs = @()
            if ($Serial) { $adbArgs += @('-s', $Serial) }
            $adbArgs += @('forward', "tcp:$Port", "tcp:$Port")
            & adb @adbArgs | Out-Null
        }
        if (-not $BaseUrl) { $BaseUrl = "http://127.0.0.1:$Port" }
    }
    $script:SubsystemBaseUrl = $BaseUrl.TrimEnd('/')
    try {
        $apps = Invoke-RestMethod -Uri "$script:SubsystemBaseUrl/apps" -TimeoutSec 5
        [pscustomobject]@{ Connected = $true; BaseUrl = $script:SubsystemBaseUrl; Apps = $apps }
    } catch {
        $script:SubsystemBaseUrl = $null
        throw "Could not reach Subsystem at $BaseUrl. Is the app running and is 'adb forward' set up? $_"
    }
}

function Invoke-Subsystem {
    [CmdletBinding()]
    param([Parameter(Mandatory, Position = 0, ValueFromPipeline)] [string]$Command)
    process {
        if (-not $script:SubsystemBaseUrl) { throw "Not connected. Run Connect-Subsystem first." }
        $resp = Invoke-RestMethod -Uri "$script:SubsystemBaseUrl/api/exec" -Method Post -Body $Command -ContentType 'text/plain'
        $resp
    }
}

function Invoke-SubsystemObject {
    # Object-fidelity sibling of Invoke-Subsystem: rides /clixml (the \Capability\Remoting\Clixml mount)
    # and deserializes the CLIXML response into LIVE PSObjects — full type + stream fidelity, so the result
    # pipes/sorts/selects like a local object instead of flattened JSON. A real PowerShell session into the
    # device runspace. Requires the capability enabled on device (it is by default; loopback-only).
    [CmdletBinding()]
    param([Parameter(Mandatory, Position = 0, ValueFromPipeline)] [string]$Command)
    process {
        if (-not $script:SubsystemBaseUrl) { throw "Not connected. Run Connect-Subsystem first." }
        $xml = Invoke-RestMethod -Uri "$script:SubsystemBaseUrl/clixml" -Method Post -Body $Command -ContentType 'text/plain'
        if ($xml -is [string]) { [System.Management.Automation.PSSerializer]::Deserialize($xml) } else { $xml }
    }
}

function Invoke-SubsystemAgent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, Position = 0)] [string]$Message,
        [switch]$Raw    # print every frame exactly as the WebView receives it (debugging the protocol)
    )
    $answer = [Text.StringBuilder]::new()
    InvokeWsFrames -Path '/agent' -Send @{ type = 'chat'; text = $Message } `
        -OnFrame {
            param($f)
            if ($Raw) { $f | ConvertTo-Json -Compress -Depth 6 | Write-Host -ForegroundColor DarkGray; return }
            switch ($f.type) {
                'status'    { Write-Host "[$($f.state)] $($f.text)" -ForegroundColor DarkCyan }
                'thinking'  { Write-Host $f.text -NoNewline -ForegroundColor DarkGray }  # reserved
                'token'     { Write-Host $f.text -NoNewline; [void]$answer.Append($f.text) }
                'error'     { Write-Host "`n[error] $($f.text)" -ForegroundColor Red }
            }
        } `
        -Until { param($f) $f.type -eq 'done' -or $f.type -eq 'error' }
    if (-not $Raw) { Write-Host '' ; return $answer.ToString() }
}

function Get-SubsystemModel {
    [CmdletBinding()] param()
    $models = $null
    InvokeWsFrames -Path '/models' -Send @{ action = 'list' } `
        -OnFrame { param($f) if ($f.type -eq 'manifest') { $script:__m = $f.models } } `
        -Until { param($f) $f.type -eq 'manifest' }
    $script:__m
}

function Install-SubsystemModel {
    [CmdletBinding()] param([Parameter(Mandatory, Position = 0)] [string]$Id)
    InvokeWsFrames -Path '/models' -Send @{ action = 'download'; id = $Id } `
        -OnFrame {
            param($f)
            switch ($f.type) {
                'progress' { Write-Progress -Activity "Downloading $Id" -Status $f.text -PercentComplete ($f.pct ?? 0) }
                'done'     { Write-Progress -Activity "Downloading $Id" -Completed; Write-Host "Installed $Id." -ForegroundColor Green }
                'error'    { Write-Host "[error] $($f.text)" -ForegroundColor Red }
            }
        } `
        -Until { param($f) $f.type -eq 'done' -or $f.type -eq 'error' }
}

function Remove-SubsystemModel {
    [CmdletBinding()] param([Parameter(Mandatory, Position = 0)] [string]$Id)
    InvokeWsFrames -Path '/models' -Send @{ action = 'delete'; id = $Id } `
        -OnFrame { param($f) if ($f.type -eq 'manifest') { $script:__m = $f.models } } `
        -Until { param($f) $f.type -eq 'manifest' }
    Write-Host "Removed $Id (if present)." -ForegroundColor Yellow
}

function Measure-SubsystemModel {
    [CmdletBinding()]
    param([string]$Prompt = 'Say hello in one short sentence.')
    $result = $null
    InvokeWsFrames -Path '/agent' -Send @{ type = 'profile'; prompt = $Prompt } `
        -OnFrame {
            param($f)
            if ($f.type -eq 'status') { Write-Host "[$($f.state)] $($f.text)" -ForegroundColor DarkCyan }
            if ($f.type -eq 'benchmark') { $script:__b = $f }
            if ($f.type -eq 'error') { Write-Host "[error] $($f.text)" -ForegroundColor Red }
        } `
        -Until { param($f) $f.type -eq 'benchmark' -or $f.type -eq 'error' }
    $b = $script:__b
    if (-not $b) { return }
    [pscustomobject]@{
        Model              = $b.model
        Backend            = $b.backend
        InitSeconds        = $b.initSeconds
        TimeToFirstTokenS  = $b.timeToFirstTokenSeconds
        PrefillTokens      = $b.prefillTokens
        PrefillTokPerSec   = $b.prefillTokensPerSecond
        DecodeTokens       = $b.decodeTokens
        DecodeTokPerSec    = $b.decodeTokensPerSecond
        WallMs             = $b.wallMs
        ResponseChars      = $b.chars
    }
}

Export-ModuleMember -Function Connect-Subsystem, Invoke-Subsystem, Invoke-SubsystemObject, Invoke-SubsystemAgent,
    Get-SubsystemModel, Install-SubsystemModel, Remove-SubsystemModel, Measure-SubsystemModel
