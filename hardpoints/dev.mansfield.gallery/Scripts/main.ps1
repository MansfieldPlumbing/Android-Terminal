$allPatterns = @(
    'Hero and body hierarchy'
    'Single-line native input'
    'Compact command bar'
    'Object-backed native list'
    'Multiline native text area'
    'Compact status bar'
)

$refresh = {
    $query = $UI.query.Text.Trim()
    $items = if ($query) { @($allPatterns | Where-Object { $_ -like "*$query*" }) } else { $allPatterns }
    $UI.patterns.Items = $items
    $UI.status.Text = "$($items.Count) PATTERNS"
}.GetNewClosure()

$UI.query.Changed = { & $refresh }.GetNewClosure()
$UI.clear.Click = {
    $UI.query.Text = ''
    $UI.notes.Text = ''
    & $refresh
}.GetNewClosure()
$UI.accept.Click = { $UI.status.Text = 'ACCEPTED' }.GetNewClosure()
$UI.patterns.Invoked = {
    param($Event)
    $UI.notes.Text = [string]$Event.Item
    $UI.status.Text = 'SELECTED'
}.GetNewClosure()

& $refresh
