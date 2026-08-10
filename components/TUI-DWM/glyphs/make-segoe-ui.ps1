# 1. Define Runtime Paths
$OutputDir = "C:\bin\msdf-atlas-gen"
$ToolPath  = Join-Path $OutputDir "msdf-atlas-gen.exe"
$Mdl2Font  = "C:\Windows\Fonts\segmdl2.ttf"
$EmojiFont = "C:\Windows\Fonts\seguiemj.ttf"

Write-Host "Initializing Segoe UI Asset Generation..." -ForegroundColor Cyan

# Verify Executable
if (-not (Test-Path $ToolPath)) {
    Write-Error "Could not find msdf-atlas-gen.exe at $ToolPath"
    return
}

# Verify Segoe Font Files
if (-not (Test-Path $Mdl2Font)) {
    Write-Error "Segoe MDL2 Assets font not found at $Mdl2Font"
    return
}
if (-not (Test-Path $EmojiFont)) {
    Write-Error "Segoe UI Emoji font not found at $EmojiFont"
    return
}

# 2. Write Out Custom Charset Definitions (UTF-8 No BOM)
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# Hex maps for Segoe MDL2 (Minimize, Maximize, Restore, Close, Folders, Chevrons, Wifi, Battery, Clock, Start)
$Mdl2Set = "0xE921, 0xE922, 0xE923, 0xE8BB, 0xE713, 0xE8B7, 0xE7C3, 0xE70D, 0xE70E, 0xE76B, 0xE76C, 0xE701, 0xE767, 0xE83F, 0xE787, 0xE121, 0xE782"
$Mdl2Path = Join-Path $OutputDir "mdl2-charset.txt"
[System.IO.File]::WriteAllText($Mdl2Path, $Mdl2Set, $Utf8NoBom)

# Hex maps for Segoe UI Emoji (Universal Dev Emojis + Standard UI Indicators)
$EmojiSet = "0x1F4C1, 0x1F4C2, 0x1F4C4, 0x1F4C3, 0x1F5D2, 0x1F4DD, 0x1F4BE, 0x1F4BF, 0x1F5A8, 0x1F4E5, 0x1F4E4, 0x1F4E6, 0x1F3F7, 0x1F4CC, 0x1F4CD, 0x1F4CE, 0x2709, 0x1F4E9, 0x1F4E7, 0x1F5D1, 0x1F50D, 0x1F50E, 0x1F517, 0x1F4BB, 0x1F5A5, 0x1F4F1, 0x1F50B, 0x1F50C, 0x2699, 0x1F6E0, 0x1F527, 0x1F528, 0x26CF, 0x1F512, 0x1F513, 0x1F511, 0x1F5DD, 0x1F6E1, 0x1F514, 0x1F515, 0x1F4F6, 0x1F310, 0x1F4E1, 0x1F4A1, 0x1F526, 0x1F680, 0x1F525, 0x26A1, 0x1F9EA, 0x1F52C, 0x1F9EC, 0x26A0, 0x26D4, 0x1F6AB, 0x1F3AF, 0x1F6A9, 0x1F3F3, 0x1F3C1, 0x23F1, 0x23F2, 0x1F570, 0x231B, 0x23F3, 0x1F4C5, 0x1F4C6, 0x1F4C8, 0x1F4C9, 0x1F4CA, 0x1F4CB, 0x2714, 0x274C, 0x2705, 0x274E, 0x2795, 0x2796, 0x2716, 0x2797, 0x2753, 0x2754, 0x2755, 0x2757, 0x2B55, 0x1F534, 0x1F7E2, 0x1F535, 0x1F7E1, 0x1F533, 0x1F532, 0x1F480, 0x2620, 0x1F47D, 0x1F47E, 0x1F916, 0x1F44D, 0x1F44E, 0x1F44A, 0x270A, 0x1F91D, 0x1F44F, 0x1F64C, 0x1F450, 0x1F600, 0x1F60A, 0x1F609, 0x1F60E, 0x1F914"
$EmojiPath = Join-Path $OutputDir "emoji-charset.txt"
[System.IO.File]::WriteAllText($EmojiPath, $EmojiSet, $Utf8NoBom)

# 3. Compile MSDF Atlases
$Stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

# Part A: UI Chrome (Segoe MDL2 Assets - 256x256)
Write-Host "`n[1/2] Compiling Segoe MDL2 Assets Atlas (256x256)..." -ForegroundColor Yellow
$Mdl2Args = "-font `"$Mdl2Font`" -charset `"$Mdl2Path`" -type msdf -pxrange 4 -dimensions 256 256 -format png -imageout `"$OutputDir\cascadia-ui-chrome-atlas.png`" -json `"$OutputDir\cascadia-ui-chrome-metrics.json`""
Start-Process -FilePath $ToolPath -ArgumentList $Mdl2Args -Wait -NoNewWindow

# Part B: Flat Emojis (Segoe UI Emoji - 1024x1024)
Write-Host "[2/2] Compiling Segoe UI Emoji Atlas (1024x1024)..." -ForegroundColor Yellow
$EmojiArgs = "-font `"$EmojiFont`" -charset `"$EmojiPath`" -type msdf -pxrange 4 -dimensions 1024 1024 -format png -imageout `"$OutputDir\cascadia-emoji-atlas.png`" -json `"$ScriptDir\cascadia-emoji-metrics.json`""
Start-Process -FilePath $ToolPath -ArgumentList $EmojiArgs -Wait -NoNewWindow

$Stopwatch.Stop()
Write-Host "`nSegoe compilations completed successfully in $([math]::round($Stopwatch.Elapsed.TotalSeconds, 2)) seconds!" -ForegroundColor Green

# 4. Status Output
Write-Host "`nGenerated Vector Assets on Disk:" -ForegroundColor Cyan
Get-ChildItem -Path $OutputDir -Filter "cascadia-*" | Select-Object Name, @{Name="Size (KB)"; Expression={[math]::round($_.Length / 1kb, 2)}}