$UI | Add-Member -NotePropertyName _Edit -NotePropertyValue ([pscustomobject]@{
    Path = Join-Path $HOME 'untitled.ps1'
    DisplayPath = 'Home:\untitled.ps1'
    CleanText = ''
    CleanStatus = 'UNTITLED'
    Dirty = $false
}) -Force

$resolvePath = {
    param([string]$DisplayPath)

    $candidate = $DisplayPath.Trim()
    if ([string]::IsNullOrWhiteSpace($candidate)) { throw 'Enter a file path.' }
    if ($candidate -match '^Home:[\\/]*(.*)$') {
        $relative = $Matches[1] -replace '^[\\/]+', ''
        if ([string]::IsNullOrWhiteSpace($relative)) { throw 'Choose a file inside Home:\.' }
        return [IO.Path]::GetFullPath((Join-Path $HOME $relative))
    }
    return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($candidate)
}.GetNewClosure()

$updateSize = {
    $bytes = [Text.Encoding]::UTF8.GetByteCount($UI.editor.Text)
    $UI.size.Text = if ($bytes -lt 1KB) { "$bytes B" } else { '{0:N1} KB' -f ($bytes / 1KB) }
}.GetNewClosure()

$updateDocumentState = {
    $contentChanged = $UI.editor.Text -cne $UI._Edit.CleanText
    $pathChanged = $UI.path.Text -cne $UI._Edit.DisplayPath
    $UI._Edit.Dirty = $contentChanged -or $pathChanged
    $UI.documentState.Text = if ($UI._Edit.Dirty) { 'MODIFIED' } else { $UI._Edit.CleanStatus }
    $UI.saveDocument.Enabled = $UI._Edit.Dirty -or $UI._Edit.CleanStatus -eq 'UNTITLED'
    & $updateSize
}.GetNewClosure()

$markClean = {
    param([string]$ResolvedPath, [string]$DisplayPath, [string]$Status)

    $UI._Edit.Path = $ResolvedPath
    $UI._Edit.DisplayPath = $DisplayPath
    $UI._Edit.CleanText = $UI.editor.Text
    $UI._Edit.CleanStatus = $Status
    $UI._Edit.Dirty = $false
    $UI.documentState.Text = $Status
    $UI.saveDocument.Enabled = $false
    & $updateSize
}.GetNewClosure()

$UI.newDocument.Click = {
    param($Event)

    $UI._Edit.Path = Join-Path $HOME 'untitled.ps1'
    $UI._Edit.DisplayPath = 'Home:\untitled.ps1'
    $UI._Edit.CleanText = ''
    $UI._Edit.CleanStatus = 'UNTITLED'
    $UI._Edit.Dirty = $false
    $UI.path.Text = $UI._Edit.DisplayPath
    $UI.editor.Text = ''
    $UI.documentState.Text = 'UNTITLED'
    $UI.saveDocument.Enabled = $true
    $UI.size.Text = '0 B'
}.GetNewClosure()

$UI.openDocument.Click = {
    param($Event)

    try {
        $displayPath = $UI.path.Text
        $resolvedPath = & $resolvePath $displayPath
        if (-not [IO.File]::Exists($resolvedPath)) { throw "File not found: $displayPath" }
        $UI.editor.Text = [IO.File]::ReadAllText($resolvedPath)
        & $markClean $resolvedPath $displayPath 'OPENED'
    }
    catch {
        $UI.documentState.Text = 'ERROR: ' + $_.Exception.Message
    }
}.GetNewClosure()

$UI.saveDocument.Click = {
    param($Event)

    try {
        $displayPath = $UI.path.Text
        $resolvedPath = & $resolvePath $displayPath
        $parent = [IO.Path]::GetDirectoryName($resolvedPath)
        if (-not [IO.Directory]::Exists($parent)) { throw "Folder not found: $parent" }
        [IO.File]::WriteAllText($resolvedPath, $UI.editor.Text, [Text.UTF8Encoding]::new($false))
        & $markClean $resolvedPath $displayPath 'SAVED'
    }
    catch {
        $UI.documentState.Text = 'ERROR: ' + $_.Exception.Message
    }
}.GetNewClosure()

$UI.editor.Changed = {
    param($Event)

    & $updateDocumentState
}.GetNewClosure()

$UI.path.Changed = {
    param($Event)

    & $updateDocumentState
}.GetNewClosure()

$UI.editor.CursorChanged = {
    param($Event)

    $UI.cursor.Text = "Ln $($Event.Value.Line), Col $($Event.Value.Column)"
}.GetNewClosure()

$UI.path.Text = $UI._Edit.DisplayPath
$UI.documentState.Text = 'UNTITLED'
$UI.saveDocument.Enabled = $true
