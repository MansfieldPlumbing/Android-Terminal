# Terminal settings. This is intentionally just PowerShell for now.
# The file is copied to $env:HOME/settings.ps1 on first launch and then preserved.
$NativeConsoleSettings = @{
    Background = '#012456'
    Foreground = '#F5F5F5'
    InputBackground = '#012456'
    InputForeground = '#FFFFFF'
    HintForeground = '#808890'
    FontSize = 14
    Scrollback = 2000
    PingerSize = 112
    PingerInterval = 2000
    Prompt = 'PS {path}> '
    AllowDragons = $false
    SettingsVersion = 8
}
