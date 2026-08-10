[CmdletBinding()]
param(
    [Parameter(ValueFromPipeline = $true, Mandatory = $true)]
    [string[]]$InputObject,
    
    [Parameter()]
    [switch]$AsKeyValuePair
)

begin {
    $results = [System.Collections.Generic.List[PSCustomObject]]::new()
    $currentBlock = [ordered]@{}
    $hasData = $false
}

process {
    foreach ($item in $InputObject) {
        if ($null -eq $item) { continue }
        foreach ($line in ($item -split "\r?\n")) {
            if ([string]::IsNullOrWhiteSpace($line)) {
                if ($hasData) {
                    if ($AsKeyValuePair) {
                        foreach ($key in $currentBlock.Keys) {
                            $results.Add([pscustomobject]@{ Key = $key; Value = $currentBlock[$key] })
                        }
                    } else {
                        $results.Add([pscustomobject]$currentBlock)
                    }
                    $currentBlock = [ordered]@{}
                    $hasData = $false
                }
                continue
            }
            
            # Match [key]: [value] (e.g. getprop)
            if ($line -match '^\s*\[([^\]]+)\]:\s*\[(.*)\]\s*$') {
                $key = $Matches[1].Trim()
                $val = $Matches[2].Trim()
                $currentBlock[$key] = $val
                $hasData = $true
            }
            # Match key: value or key=value
            elseif ($line -match '^\s*([^=:]+?)\s*[:=]\s*(.*)$') {
                $key = $Matches[1].Trim()
                $val = $Matches[2].Trim()
                $currentBlock[$key] = $val
                $hasData = $true
            }
        }
    }
}

end {
    if ($hasData) {
        if ($AsKeyValuePair) {
            foreach ($key in $currentBlock.Keys) {
                $results.Add([pscustomobject]@{ Key = $key; Value = $currentBlock[$key] })
            }
        } else {
            $results.Add([pscustomobject]$currentBlock)
        }
    }
    $results
}
