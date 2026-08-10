# shell-functions.ps1 — User-tier shell-convenience functions, loaded into the InitialSessionState
# at runspace init (SubsystemAliases.Load parses this file's FunctionDefinitionAst). These are larval
# convenience, NOT canon cmdlets — so they live as script DATA shipped as an APK asset
# (wwwroot/cli/shell-functions.ps1), never as string literals baked into .cs (SS001). Pillars are
# compiled PSCmdlets; these are the cmd.exe/coreutils muscle-memory shims a shell is expected to have.
#
# Edit the bodies here — they are real PowerShell. Every runspace built from the shared ISS (the
# terminal, named sessions, the /api/exec pool) inherits them by construction.

function cd {
    param([Parameter(ValueFromRemainingArguments = $true)]$Path)
    if ($Path) { Set-Location "$Path" } else { Set-Location $env:HOME }
}

function cd.. { Set-Location .. }

function cd... { Set-Location ../.. }

function Clear-Host { Write-Host -NoNewline "`e[2J`e[H" }

function clear { Clear-Host }

function cls { Clear-Host }

function mkdir {
    param([Parameter(ValueFromPipeline = $true)]$Path)
    New-Item -ItemType Directory -Path $Path
}

function deltree {
    param([Parameter(ValueFromPipeline = $true)]$Path)
    Remove-Item -Recurse -Force $Path
}

function ipconfig {
    [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() |
        Where-Object { $_.OperationalStatus -eq 'Up' } |
        ForEach-Object { $_.GetIPProperties() } |
        ForEach-Object { $_.UnicastAddresses } |
        Where-Object { $_.Address.AddressFamily -eq 'InterNetwork' -and -not [System.Net.IPAddress]::IsLoopback($_.Address) } |
        Select-Object -Property Address, IPv4Mask
}

function settings { & "$env:HOME/settings.ps1" }
