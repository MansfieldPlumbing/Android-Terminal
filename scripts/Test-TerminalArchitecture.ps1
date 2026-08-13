[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$engine = Join-Path $repository 'src\Terminal.VT'
$bos = Join-Path $repository 'src\Terminal.BOS'
$surface = Join-Path $repository 'src\Terminal.Android\Surface'
$failures = [Collections.Generic.List[string]]::new()

foreach ($file in Get-ChildItem -LiteralPath $engine -Filter '*.cs' -Recurse) {
    $matches = Select-String -LiteralPath $file.FullName -Pattern '^\s*using\s+(Android|Java|Javax)(\.|;)' -CaseSensitive
    foreach ($match in $matches) {
        $failures.Add("$($file.FullName):$($match.LineNumber): Terminal.VT cannot reference Android or Java.")
    }
}

foreach ($file in Get-ChildItem -LiteralPath $bos -Filter '*.cs' -Recurse) {
    $matches = Select-String -LiteralPath $file.FullName -Pattern '^\s*using\s+(Android|Java|Javax|System\.Management\.Automation)(\.|;)' -CaseSensitive
    foreach ($match in $matches) {
        $failures.Add("$($file.FullName):$($match.LineNumber): Terminal.BOS cannot reference Android, Java, or PowerShell.")
    }
}

foreach ($file in Get-ChildItem -LiteralPath $surface -Filter '*.cs' -Recurse |
    Where-Object Name -ne 'SurfaceTheme.cs') {
    $matches = Select-String -LiteralPath $file.FullName -Pattern 'ParseColor\s*\(\s*"#[0-9A-Fa-f]{6,8}"' -CaseSensitive
    foreach ($match in $matches) {
        $failures.Add("$($file.FullName):$($match.LineNumber): Surface colors must be named in SurfaceTheme.")
    }
}

if ($failures.Count -gt 0) { throw ($failures -join [Environment]::NewLine) }
Write-Host 'Terminal architecture contract passed.'
