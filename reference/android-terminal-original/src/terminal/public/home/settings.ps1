$configPath = Join-Path $env:HOME "config.ini"

$State = @{
    Selected = 0
    LastWidth = 0
    Config = @{
        FontSize = "14"; CursorShape = "Bar"; CursorBlink = "true"
        BackgroundColor = "#0C0C0C"; ForegroundColor = "#CCCCCC"
    }
    Options = @(
        @{ Key = "FontSize"; Label = "Font Size"; Vals = @("10","12","14","16","18","20","24") }
        @{ Key = "CursorShape"; Label = "Cursor Shape"; Vals = @("Bar","Block","Underline") }
        @{ Key = "CursorBlink"; Label = "Cursor Blink"; Vals = @("true","false") }
        @{ Key = "BackgroundColor"; Label = "Background"; Vals = @("#0C0C0C","#1E1E2E","#012456","#1A1A2E","#000000") }
        @{ Key = "ForegroundColor"; Label = "Foreground"; Vals = @("#CCCCCC","#CDD6F4","#F8F8F2","#E0DEF4","#FFFFFF") }
        @{ Key = "SAVE"; Label = "[ SAVE & EXIT ]"; Vals = @() }
        @{ Key = "EXIT"; Label = "[ CANCEL ]"; Vals = @() }
    )
}

if (Test-Path $configPath) {
    Get-Content $configPath | ForEach-Object {
        if ($_ -match '^\s*(FontSize|CursorShape|CursorBlink|BackgroundColor|ForegroundColor|Background|Foreground)\s*=\s*(.+)$') {
            $k = $Matches[1]; if ($k -eq 'Background') { $k = 'BackgroundColor' }; if ($k -eq 'Foreground') { $k = 'ForegroundColor' }
            $State.Config[$k] = $Matches[2].Trim()
        }
    }
}

function Draw-UI {
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.Append("`e[?25l`e[2J`e[H")
    
    $w = $Host.UI.RawUI.WindowSize.Width; if ($w -lt 40) { $w = 40 }
    $State.LastWidth = $w

    $title = " TERMINAL SETTINGS "
    $pad = [Math]::Max(0, [Math]::Floor(($w - $title.Length - 2) / 2))
    $line = ("─" * $pad) + $title + ("─" * ($w - $pad - $title.Length - 2))
    
    [void]$sb.Append("`e[38;5;51m╭$line╮`e[0m`r`n")
    
    for ($i = 0; $i -lt $State.Options.Count; $i++) {
        $opt = $State.Options[$i]; $isSel = ($i -eq $State.Selected)
        $prefix = if ($isSel) { "`e[38;5;226m› `e[38;5;255m" } else { "  `e[38;5;244m" }
        $label = $opt.Label.PadRight(16)
        
        if ($opt.Key -in @("SAVE", "EXIT")) {
            $valStr = ""; $prefix = if ($isSel) { "`e[38;5;46m› `e[38;5;255m" } else { "  `e[38;5;244m" }
        } else {
            $curVal = $State.Config[$opt.Key]
            $valStr = if ($isSel) { "`e[38;5;51m◄ $curVal ►`e[0m" } else { "`e[38;5;240m  $curVal  `e[0m" }
        }
        
        $rawLen = 2 + 16 + 1 + (if($opt.Key -in @("SAVE","EXIT")){0}else{ $State.Config[$opt.Key].Length + 4 })
        $rpad = [Math]::Max(0, $w - 2 - $rawLen)
        [void]$sb.Append("`e[38;5;51m│`e[0m$prefix$label `e[0m$valStr$(' ' * $rpad)`e[38;5;51m│`e[0m`r`n")
    }
    
    [void]$sb.Append("`e[38;5;51m├$("─" * ($w - 2))┤`e[0m`r`n")
    $hint = " D-Pad: Navigate/Change   Enter: Select "
    [void]$sb.Append("`e[38;5;51m│`e[38;5;240m$hint$(' ' * ($w - 2 - $hint.Length))`e[38;5;51m│`e[0m`r`n")
    [void]$sb.Append("`e[38;5;51m╰$("─" * ($w - 2))╯`e[0m`r`n")
    
    [Console]::Write($sb.ToString())
}

try {
    Draw-UI
    while ($true) {
        if ($Host.UI.RawUI.WindowSize.Width -ne $State.LastWidth) { Draw-UI }

        if ($Host.UI.RawUI.KeyAvailable) {
            $k = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown").VirtualKeyCode
            $opt = $State.Options[$State.Selected]

            if ($k -eq 38) { $State.Selected = [Math]::Max(0, $State.Selected - 1) } 
            if ($k -eq 40) { $State.Selected = [Math]::Min($State.Options.Count - 1, $State.Selected + 1) } 
            
            if ($k -eq 37 -or $k -eq 39) { 
                if ($opt.Vals.Count -gt 0) {
                    $idx = $opt.Vals.IndexOf($State.Config[$opt.Key])
                    if ($idx -lt 0) { $idx = 0 }
                    if ($k -eq 37) { $idx = ($idx - 1 + $opt.Vals.Count) % $opt.Vals.Count }
                    if ($k -eq 39) { $idx = ($idx + 1) % $opt.Vals.Count }
                    $State.Config[$opt.Key] = $opt.Vals[$idx]
                }
            }

            if ($k -eq 13) { 
                if ($opt.Key -eq "SAVE") {
                    if (Test-Path $configPath) {
                        $txt = Get-Content $configPath -Raw
                        foreach ($key in $State.Config.Keys) {
                            $mapped = $key; if ($key -eq 'BackgroundColor') { $mapped = 'Background' }; if ($key -eq 'ForegroundColor') { $mapped = 'Foreground' }
                            if ($txt -match "(?m)^\s*$mapped\s*=.*") {
                                $txt = $txt -replace "(?m)^\s*$mapped\s*=.*", "$mapped=$($State.Config[$key])"
                            } else { $txt += "`r`n$mapped=$($State.Config[$key])" }
                        }
                        Set-Content $configPath $txt
                    }
                    break
                }
                if ($opt.Key -eq "EXIT") { break }
            }
            if ($k -eq 27) { break }
            Draw-UI
        } else {
            Start-Sleep -Milliseconds 30
        }
    }
} finally {
    [Console]::Write("`e[?25h`e[2J`e[H")
}
