using System.Management.Automation.Runspaces;

namespace TerminalApp {
    public static class SubsystemAliases {
        public static readonly (string Alias, string Target)[] CoreMappings = new[] {
            ("sl", "Set-Location"), ("chdir", "Set-Location"), ("pwd", "Get-Location"),
            ("pushd", "Push-Location"), ("popd", "Pop-Location"), ("ls", "Get-ChildItem"), ("dir", "Get-ChildItem"),
            ("gci", "Get-ChildItem"), ("cat", "Get-Content"), ("type", "Get-Content"), ("gc", "Get-Content"),
            ("echo", "Write-Output"), ("write", "Write-Output"), ("cp", "Copy-Item"), ("copy", "Copy-Item"),
            ("cpi", "Copy-Item"), ("mv", "Move-Item"), ("move", "Move-Item"), ("mi", "Move-Item"),
            ("rm", "Remove-Item"), ("del", "Remove-Item"), ("rd", "Remove-Item"), ("ri", "Remove-Item"),
            ("rmdir", "Remove-Item"), ("erase", "Remove-Item"), ("ren", "Rename-Item"), ("rni", "Rename-Item"), 
            ("h", "Get-History"), ("history", "Get-History"), 
            ("ghy", "Get-History"), ("clhy", "Clear-History"), ("man", "Get-Help"), ("help", "Get-Help"), 
            ("gip", "Get-Help"), ("which", "Get-Command"), ("gcm", "Get-Command"), ("ps", "Get-Process"), 
            ("gps", "Get-Process"), ("kill", "Stop-Process"), ("sps", "Stop-Process"), ("grep", "Select-String"), 
            ("sls", "Select-String"), ("sort", "Sort-Object"), ("tee", "Tee-Object"), ("diff", "Compare-Object"), 
            ("compare", "Compare-Object"), ("measure", "Measure-Object"), ("curl", "Invoke-WebRequest"), 
            ("wget", "Invoke-WebRequest"), ("iwr", "Invoke-WebRequest"), ("irm", "Invoke-RestMethod"), 
            ("alias", "Get-Alias"), ("gal", "Get-Alias"), ("sal", "Set-Alias"),
            ("foreach", "ForEach-Object"), ("%", "ForEach-Object"), 
            ("where", "Where-Object"), ("?", "Where-Object"), ("select", "Select-Object")
        };

        public static void Load(InitialSessionState iss) {
            foreach (var mapping in CoreMappings) iss.Commands.Add(new SessionStateAliasEntry(mapping.Alias, mapping.Target, ""));
            
            iss.Commands.Add(new SessionStateFunctionEntry("cd", "param([Parameter(ValueFromRemainingArguments=$true)]$Path); if ($Path) { Set-Location \"$Path\" } else { Set-Location $env:HOME }"));
            iss.Commands.Add(new SessionStateFunctionEntry("cd..", "Set-Location .."));
            iss.Commands.Add(new SessionStateFunctionEntry("cd...", "Set-Location ../.."));
            
            iss.Commands.Add(new SessionStateFunctionEntry("Clear-Host", "Write-Host -NoNewline \"`e[2J`e[H\""));
            iss.Commands.Add(new SessionStateFunctionEntry("clear", "Clear-Host"));
            iss.Commands.Add(new SessionStateFunctionEntry("cls", "Clear-Host"));

            iss.Commands.Add(new SessionStateFunctionEntry("mkdir", "param([Parameter(ValueFromPipeline=$true)]$Path); New-Item -ItemType Directory -Path $Path"));
            iss.Commands.Add(new SessionStateFunctionEntry("deltree", "param([Parameter(ValueFromPipeline=$true)]$Path); Remove-Item -Recurse -Force $Path"));
            iss.Commands.Add(new SessionStateFunctionEntry("ipconfig", "([System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | Where-Object { $_.OperationalStatus -eq 'Up' } | ForEach-Object { $_.GetIPProperties() } | ForEach-Object { $_.UnicastAddresses } | Where-Object { $_.Address.AddressFamily -eq 'InterNetwork' -and -not [System.Net.IPAddress]::IsLoopback($_.Address) } | Select-Object -Property Address, IPv4Mask)"));
            iss.Commands.Add(new SessionStateFunctionEntry("settings", "& \"$env:HOME/settings.ps1\""));

            // Canonical functions with approved Verb-Noun naming
            iss.Commands.Add(new SessionStateFunctionEntry("Invoke-Vibration", "param([int]$Duration=200); [TerminalApp.VirtualObjectManager]::Vibrate($Duration)"));
            iss.Commands.Add(new SessionStateFunctionEntry("Show-Toast", "param([Parameter(Mandatory=$true)][string]$Message, [switch]$Long); [TerminalApp.VirtualObjectManager]::ShowToast($Message, [bool]$Long)"));
            iss.Commands.Add(new SessionStateFunctionEntry("Set-Flashlight", "param([string]$State='Toggle'); [TerminalApp.VirtualObjectManager]::SetFlashlight($State); Write-Host \"Flashlight state set to: $State\" -ForegroundColor Yellow"));



            // Register custom Get-Help function mapping to Compiled HelpSystem
            iss.Commands.Add(new SessionStateFunctionEntry("Get-Help", @"
param([string]$Name)
if (-not $Name) {
    Write-Host 'Usage: Get-Help <topic>' -ForegroundColor Cyan
    Write-Host 'Available topics include about_* help files.' -ForegroundColor Gray
    return
}
$helpText = [TerminalApp.HelpSystem]::GetHelp($Name)
if ($helpText.StartsWith('Multiple topics match') -or $helpText.StartsWith('Usage:')) {
    Write-Host $helpText -ForegroundColor Yellow
} elseif ($helpText.StartsWith('Help topic') -and $helpText.EndsWith('not found.')) {
    Write-Warning $helpText
} else {
    Write-Output $helpText
}
"));
        }
    }
}
