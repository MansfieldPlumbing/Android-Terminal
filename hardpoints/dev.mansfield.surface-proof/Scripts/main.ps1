$refresh = {
    param($Event)

    $query = if ($Event.Name -eq 'Changed') { $Event.NewValue } else { $UI.query.Text }
    try {
        # Application and ExternalScript discovery crosses PowerShell's native
        # executable probe. Keep this palette on the managed command surface.
        $items = @(
            Get-Command -Name "*$query*" -CommandType Alias,Function,Filter,Cmdlet -ErrorAction Stop |
                Select-Object -First 50
        )
    }
    catch {
        $UI.results.Items = @()
        $UI.status.Text = $_.Exception.Message
        return
    }

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
