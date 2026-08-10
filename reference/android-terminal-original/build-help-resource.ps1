# build-help-resource.ps1
$helpDir = Join-Path $PSScriptRoot "src/pshelp"
$destFile = Join-Path $PSScriptRoot "src/runspace/pshelp.json"

if (-not (Test-Path $helpDir)) {
    Write-Error "Source directory $helpDir not found."
    exit 1
}

$helpData = [ordered]@{ }
Get-ChildItem $helpDir -Filter "*.help.txt" | ForEach-Object {
    $topicName = $_.BaseName -replace '\.help$', ''
    $content = Get-Content $_.FullName -Raw
    $helpData[$topicName] = $content
}

$json = ConvertTo-Json $helpData -Depth 10
[System.IO.File]::WriteAllText($destFile, $json)
Write-Host "Successfully compiled $($helpData.Count) help files into $destFile"
