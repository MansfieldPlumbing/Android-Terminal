<#
.SYNOPSIS
    Optimized Universal Emoji MSDF Generation (Range-Based)
    Generates 850+ system and reaction emojis without compiler choking.
.NOTES
    Save as C:\bin\msdf-atlas-gen\make-emoji-atlas.ps1
#>

# 1. Resolve Paths
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
if ([string]::IsNullOrEmpty($ScriptDir)) { $ScriptDir = "C:\bin\msdf-atlas-gen" }

$ToolPath  = Join-Path $ScriptDir "msdf-atlas-gen.exe"
$EmojiFont = "C:\Windows\Fonts\seguiemj.ttf"

Write-Host "Initializing Range-Based Emoji Compiler..." -ForegroundColor Cyan

# Verify
if (-not (Test-Path $ToolPath)) { Write-Error "msdf-atlas-gen.exe not found"; return }
if (-not (Test-Path $EmojiFont)) { Write-Error "Segoe UI Emoji not found"; return }

# 2. Programmatically write pure Unicode ranges (UTF-8 No BOM)
# [0x1F600, 0x1F64F] -> Full Standard Emoticons (80 faces)
# [0x1F680, 0x1F6C5] -> Transport/Map Symbols (Rockets, Vehicles, Statuses)
# [0x2300,  0x23FF]  -> Miscellaneous Technical (Keyboards, Mice, Gears, Clocks)
# [0x2600,  0x26FF]  -> Miscellaneous Symbols (Warnings, Checkmarks, Card Suits, Weather)
# [0x2700,  0x27BF]  -> Dingbats (Checkmarks, Scissors, UI Arrows)
$Ranges = @"
[0x1F600, 0x1F64F]
[0x1F680, 0x1F6C5]
[0x2300, 0x23FF]
[0x2600, 0x26FF]
[0x2700, 0x27BF]
"@

$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$CharsetPath = Join-Path $ScriptDir "universal-emoji-charset.txt"
[System.IO.File]::WriteAllText($CharsetPath, $Ranges, $Utf8NoBom)
Write-Host "Targeted 850+ vector emoji/technical glyphs in charset." -ForegroundColor Green

# 3. Execute MSDF Compilation (2048x2048 to prevent overlap bleed)
$Stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
Write-Host "`nCompiling 850+ Vector Emojis (2048x2048)..." -ForegroundColor Yellow

# We pass the range-based charset file. It processes instantly.
& $ToolPath -font $EmojiFont -charset $CharsetPath -type msdf -pxrange 4 -dimensions 2048 2048 -format png -imageout "$ScriptDir\cascadia-emoji-universal-atlas.png" -json "$ScriptDir\cascadia-emoji-universal-metrics.json"

$Stopwatch.Stop()
Write-Host "`nUniversal Emoji Atlas generated successfully in $([math]::round($Stopwatch.Elapsed.TotalSeconds, 2)) seconds!" -ForegroundColor Green
Get-ChildItem -Path $ScriptDir -Filter "cascadia-emoji-universal-*" | Select-Object Name, @{Name="Size (KB)"; Expression={[math]::round($_.Length / 1kb, 2)}}