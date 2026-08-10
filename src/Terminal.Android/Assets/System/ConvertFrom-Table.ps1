[CmdletBinding()]
param(
    [Parameter(ValueFromPipeline = $true, Mandatory = $true)]
    [string[]]$InputObject,
    
    [Parameter()]
    [string[]]$Header,
    
    [Parameter()]
    [int]$Skip = 0
)

begin {
    $lines = [System.Collections.Generic.List[string]]::new()
}

process {
    foreach ($item in $InputObject) {
        if ($null -eq $item) { continue }
        foreach ($line in ($item -split "\r?\n")) {
            if (-not [string]::IsNullOrWhiteSpace($line)) {
                $lines.Add($line.Trim())
            }
        }
    }
}

end {
    if ($lines.Count -eq 0) { return }
    
    $startIndex = $Skip
    if ($null -eq $Header -or $Header.Count -eq 0) {
        if ($lines.Count -le $Skip) { return }
        $headerLine = $lines[$Skip]
        $Header = $headerLine -split '\s+'
        $startIndex = $Skip + 1
    }
    
    for ($i = $startIndex; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $parts = $line -split '\s+'
        $obj = [ordered]@{}
        for ($j = 0; $j -lt $Header.Count; $j++) {
            $colName = $Header[$j]
            if ($j -lt $parts.Count) {
                if ($j -eq ($Header.Count - 1)) {
                    # For the last column, if there are remaining parts, join them
                    $obj[$colName] = ($parts[$j..($parts.Count - 1)] -join ' ')
                } else {
                    $obj[$colName] = $parts[$j]
                }
            } else {
                $obj[$colName] = $null
            }
        }
        [pscustomobject]$obj
    }
}
