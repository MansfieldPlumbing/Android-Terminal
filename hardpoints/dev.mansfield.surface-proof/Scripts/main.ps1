$refresh = {
    param($Event)

    $query = if ($Event.Name -eq 'Changed') { $Event.NewValue } else { $UI.query.Text }
    $items = @(
        Get-Command "*$query*" |
            Select-Object -First 50
    )

    $UI.results.Items = $items
    $UI.status.Text = "$($items.Count) results"
}

$UI.search.Click = $refresh
$UI.query.Changed = $refresh

$UI.results.Invoked = {
    param($Event)
    $UI.status.Text = $Event.Item.Name
}

$UI.status.Text = 'Ready — native Views, PowerShell behavior'
