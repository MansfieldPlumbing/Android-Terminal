using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;

namespace NativePwshConsole;

[Activity(Label = "Terminal", MainLauncher = true,
    Theme = "@android:style/Theme.Material.NoActionBar",
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = Android.Content.PM.ConfigChanges.Orientation |
                           Android.Content.PM.ConfigChanges.ScreenSize |
                           Android.Content.PM.ConfigChanges.KeyboardHidden)]
public sealed class MainActivity : Activity
{
    private PowerShellSession? _session;
    private NativeConsoleView? _console;
    private EditText? _input;
    private AlertDialog? _settingsDialog;
    private ConsoleSettings _settings = new();
    private string _settingsPath = string.Empty;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.SetStatusBarColor(Color.Rgb(9, 12, 18));
        AndroidBridge.Configure(this);
        AdbLoopback.Configure(this);

        string settingsPath = SeedSettings();
        _settingsPath = settingsPath;
        try
        {
            _session = TerminalRuntime.GetOrCreate(this);
            _settings = _session.LoadSettings(settingsPath);
            TerminalSourcePolicy.DragonsEnabled = _settings.AllowDragons;
        }
        catch { _settings = new ConsoleSettings(); }

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetFitsSystemWindows(true);
        var buffer = new CellBuffer { MaxLines = _settings.Scrollback };
        _console = new NativeConsoleView(this, buffer, _settings);
        _console.ViewportChanged += (columns, rows) => _session?.SetWindowSize(columns, rows);
        root.AddView(_console, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));

        _input = new EditText(this)
        {
            Hint = "PowerShell command",
            TextSize = _settings.FontSize,
            ImeOptions = ImeAction.Send
        };
        _input.SetSingleLine(true);
        _input.SetTextColor(Color.ParseColor(_settings.InputForeground));
        _input.SetHintTextColor(Color.ParseColor(_settings.HintForeground));
        _input.SetBackgroundColor(Color.ParseColor(_settings.InputBackground));
        _input.EditorAction += OnEditorAction;
        root.AddView(_input, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        SetContentView(root);
        Window?.SetSoftInputMode(SoftInput.AdjustResize | SoftInput.StateAlwaysHidden);
        _input.RequestFocus();
        InstallLauncherShortcut();
        Oobe.ShowIfNeeded(this);
        AndroidBridge.StartSessionGuardian("PowerShell session", "Local CoreCLR runspace");
        if (Intent?.GetBooleanExtra("open_settings", false) == true) ShowSettingsMenu();

        Task.Run(() =>
        {
            try
            {
                _session ??= TerminalRuntime.GetOrCreate(this);
                _session.Output -= OnSessionOutput;
                _session.Output += OnSessionOutput;
                foreach (string diagnostic in _session.StartupDiagnostics)
                    _console.Append($"\x1b[91m{diagnostic}\x1b[0m\n");
                _console.Append($"PowerShell {PowerShellSession.EngineVersion}\n");
                _console.Append(_session.GetPromptAsync().GetAwaiter().GetResult());
            }
            catch (Exception ex) { _console.Append($"BOOT ERROR: {ex}\n"); }
        });
    }

    private void OnSessionOutput(string text) => _console?.Append(text);

    protected override void OnResume()
    {
        base.OnResume();
        AdbLoopback.Configure(this);
        AdbLoopback.ResumeDiscovery();
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        if (intent?.GetBooleanExtra("open_settings", false) == true) ShowSettingsMenu();
    }

    private void InstallLauncherShortcut()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.NMr1) return;
        var manager = (Android.Content.PM.ShortcutManager)GetSystemService(ShortcutService)!;
        var intent = new Intent(this, typeof(MainActivity));
        intent.SetAction(Intent.ActionView);
        intent.PutExtra("open_settings", true);
        var shortcut = new Android.Content.PM.ShortcutInfo.Builder(this, "settings")
            .SetShortLabel("Settings")
            .SetLongLabel("Terminal settings")
            .SetIcon(Android.Graphics.Drawables.Icon.CreateWithResource(this, Resource.Drawable.terminal_logo))
            .SetIntent(intent)
            .Build();
        manager.SetDynamicShortcuts([shortcut]);
    }

    public void ShowSettingsMenu() => ShowSettingsPage(null, body =>
    {
        body.AddView(SettingsRow("Appearance", "Theme and console colors", ShowAppearanceSettings));
        body.AddView(SettingsRow("Terminal", $"Font {_settings.FontSize:0} sp · {_settings.Scrollback:N0} history lines", ShowTerminalSettings));
        body.AddView(SettingsRow("Sessions", SessionGuardianService.IsRunning ? "Protected session running" : "Background session controls", ShowSessionSettings));
        body.AddView(SettingsRow("Permissions", AdbLoopback.IsConnected ? "Self-ADB connected" : "Optional capabilities", ShowAndroidSettings));
        body.AddView(SettingsRow("Configuration", "Edit or restore settings.ps1", ShowConfigurationSettings));
        body.AddView(SettingsRow("About", "Terminal 0.2", ShowAboutSettings));
    });

    private int SettingsDp(int value) => (int)(value * Resources!.DisplayMetrics!.Density + .5f);

    private TextView SettingsText(string text, float size = 16, string color = "#F5F5F5")
    {
        var view = new TextView(this) { Text = text, TextSize = size };
        view.SetTextColor(Color.ParseColor(color));
        return view;
    }

    private View SettingsRow(string title, string caption, Action action, string? value = null)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(SettingsDp(18), SettingsDp(14), SettingsDp(14), SettingsDp(14));
        var background = new GradientDrawable();
        background.SetColor(Color.ParseColor("#2B2D31"));
        background.SetCornerRadius(SettingsDp(8));
        row.Background = background;

        var words = new LinearLayout(this) { Orientation = Orientation.Vertical };
        words.AddView(SettingsText(title, 16));
        words.AddView(SettingsText(caption, 12, "#B6BBC7"));
        row.AddView(words, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        row.AddView(SettingsText(value ?? "›", value == null ? 24 : 13, "#B9C0CC"));
        row.Click += (_, _) => action();
        var layout = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        layout.SetMargins(SettingsDp(16), SettingsDp(3), SettingsDp(16), SettingsDp(3));
        row.LayoutParameters = layout;
        return row;
    }

    private View SettingsToggleRow(string title, string caption, bool value, Action<Switch, bool> changed)
    {
        var toggle = new Switch(this) { Checked = value };
        var row = (LinearLayout)SettingsRow(title, caption, () => toggle.Checked = !toggle.Checked, "");
        row.RemoveViewAt(row.ChildCount - 1);
        row.AddView(toggle);
        toggle.CheckedChange += (_, e) => changed(toggle, e.IsChecked);
        return row;
    }

    private void ShowSettingsPage(string? section, Action<LinearLayout> populate)
    {
        _settingsDialog?.Dismiss();
        var body = new LinearLayout(this) { Orientation = Orientation.Vertical };
        body.SetBackgroundColor(Color.ParseColor("#202124"));
        body.SetPadding(0, SettingsDp(8), 0, SettingsDp(22));
        var header = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        header.SetPadding(SettingsDp(18), SettingsDp(8), SettingsDp(12), SettingsDp(10));
        if (section == null)
            header.AddView(SettingsText("Settings", 27), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        else
        {
            var trail = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            var home = SettingsText("Settings", 14, "#9CCBFF");
            home.SetPadding(0, SettingsDp(8), SettingsDp(8), SettingsDp(8));
            home.Click += (_, _) => ShowSettingsMenu();
            trail.AddView(home);
            trail.AddView(SettingsText($"›  {section}", 14, "#B6BBC7"));
            header.AddView(trail, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        }
        var close = SettingsText("×", 28, "#E8E8E8");
        close.Gravity = GravityFlags.Center;
        close.SetPadding(SettingsDp(12), 0, SettingsDp(6), 0);
        close.Click += (_, _) => _settingsDialog?.Dismiss();
        header.AddView(close, new LinearLayout.LayoutParams(SettingsDp(48), SettingsDp(48)));
        body.AddView(header);
        populate(body);
        var scroll = new ScrollView(this); scroll.AddView(body);
        _settingsDialog = new AlertDialog.Builder(this).SetView(scroll).Create();
        _settingsDialog.Show();
        var surface = new GradientDrawable();
        surface.SetColor(Color.ParseColor("#202124"));
        surface.SetCornerRadius(SettingsDp(14));
        _settingsDialog.Window?.SetBackgroundDrawable(surface);
        _settingsDialog.Window?.SetLayout(Resources!.DisplayMetrics!.WidthPixels - SettingsDp(16),
            Resources.DisplayMetrics.HeightPixels - SettingsDp(32));
    }

    private void ShowAppearanceSettings() => ShowSettingsPage("Appearance", body =>
    {
        foreach (var theme in new[]
        {
            (Name: "Campbell PowerShell", Caption: "PowerShell blue", Background: "#012456", Foreground: "#CCCCCC"),
            (Name: "Campbell", Caption: "Windows Terminal default", Background: "#0C0C0C", Foreground: "#CCCCCC"),
            (Name: "One Half Dark", Caption: "Cool charcoal", Background: "#282C34", Foreground: "#DCDFE4"),
            (Name: "Dark+", Caption: "Visual Studio dark", Background: "#1E1E1E", Foreground: "#D4D4D4"),
            (Name: "AMOLED", Caption: "True black for low light", Background: "#000000", Foreground: "#F2F2F2"),
            (Name: "Light", Caption: "Bright workspace", Background: "#F4F4F4", Foreground: "#171717")
        })
            body.AddView(SettingsRow(theme.Name, theme.Caption,
                () => { SetTheme(theme.Background, theme.Foreground); ApplySettings(); SaveSettings(); },
                _settings.Background.Equals(theme.Background, StringComparison.OrdinalIgnoreCase) ? "On" : ""));
    });

    private void ShowTerminalSettings() => ShowSettingsPage("Terminal", body =>
    {
        var fontValue = SettingsText($"Font size  ·  {_settings.FontSize:0} sp", 14, "#B6BBC7");
        fontValue.SetPadding(SettingsDp(18), SettingsDp(12), SettingsDp(18), 0); body.AddView(fontValue);
        var font = new SeekBar(this) { Max = 22, Progress = Math.Clamp((int)_settings.FontSize - 10, 0, 22) };
        font.ProgressChanged += (_, e) => { if (!e.FromUser) return; _settings.FontSize = 10 + e.Progress; fontValue.Text = $"Font size  ·  {_settings.FontSize:0} sp"; ApplySettings(); SaveSettings(); };
        body.AddView(font);
        var historyValue = SettingsText($"Scrollback  ·  {_settings.Scrollback:N0} lines", 14, "#B6BBC7");
        historyValue.SetPadding(SettingsDp(18), SettingsDp(14), SettingsDp(18), 0); body.AddView(historyValue);
        var history = new SeekBar(this) { Max = 199, Progress = Math.Clamp(_settings.Scrollback / 100 - 1, 0, 199) };
        history.ProgressChanged += (_, e) => { if (!e.FromUser) return; _settings.Scrollback = (e.Progress + 1) * 100; historyValue.Text = $"Scrollback  ·  {_settings.Scrollback:N0} lines"; ApplySettings(); SaveSettings(); };
        body.AddView(history);
    });

    private void ShowSessionSettings() => ShowSettingsPage("Sessions", body =>
    {
        body.AddView(SettingsToggleRow("Protected PowerShell session", "Durable notification and deliberate shutdown",
            SessionGuardianService.IsRunning, (_, enabled) =>
            {
                if (enabled) AndroidBridge.StartSessionGuardian("PowerShell session", "Local CoreCLR runspace");
                else AndroidBridge.RequestStopSessionGuardian();
            }));
        body.AddView(SettingsRow("Notification controls", "Visibility and Android notification channels", () =>
        {
            var intent = new Intent(Android.Provider.Settings.ActionAppNotificationSettings);
            intent.PutExtra(Android.Provider.Settings.ExtraAppPackage, PackageName); StartActivity(intent);
        }));
    });

    private void ShowAndroidSettings() => ShowSettingsPage("Permissions", body =>
    {
        body.AddView(SettingsRow("Privacy & permissions", "Review every optional capability", () => Oobe.Show(this)));
        body.AddView(SettingsRow("Self-ADB", "Wireless-debugging shell authority", AdbLoopback.BeginSetup, AdbLoopback.IsConnected ? "Connected" : "Off"));
        body.AddView(SettingsRow("Forget self-ADB", "Delete Terminal's private pairing identity", AdbLoopback.Forget));
        body.AddView(SettingsRow("Shared file access", "Review storage access in Android Settings", () => AndroidBridge.RequestFileManagerAccess()));
    });

    private void ShowConfigurationSettings() => ShowSettingsPage("Configuration", body =>
    {
        body.AddView(SettingsRow("Edit settings.ps1", _settingsPath, () => Task.Run(() =>
        {
            if (!EditFile(_settingsPath)) return;
            RunOnUiThread(() =>
            {
                _settings = _session?.LoadSettings(_settingsPath) ?? _settings;
                TerminalSourcePolicy.DragonsEnabled = _settings.AllowDragons;
                ApplySettings();
            });
        })));
        body.AddView(SettingsRow("Restore visual defaults", "Preserves profile.ps1, scripts, files, and pairing", RestoreSettingsDefaults));
        body.AddView(SettingsToggleRow("Roslyn analyzers", "Conservative source policy", !_settings.AllowDragons,
            ToggleAnalyzers));
        body.AddView(SettingsRow("App info", "Permissions, storage, cache, and defaults", OpenAppStorageSettings));
    });

    private void ToggleAnalyzers(Switch toggle, bool enabled)
    {
        if (enabled)
        {
            _settings.AllowDragons = false;
            TerminalSourcePolicy.DragonsEnabled = false;
            SaveSettings();
            return;
        }

        new AlertDialog.Builder(this)
            .SetTitle("Disable Roslyn analyzers?")
            .SetMessage("Source policy findings will remain visible, but owner-initiated local compilation may continue past errors. " +
                "Android permissions and remote authority are unchanged.")
            .SetNegativeButton("Cancel", (_, _) => toggle.Checked = true)
            .SetPositiveButton("Disable", (_, _) =>
            {
                _settings.AllowDragons = true;
                TerminalSourcePolicy.DragonsEnabled = true;
                SaveSettings();
            }).Show();
    }

    private void ShowAboutSettings() => ShowSettingsPage("About", body =>
    {
        body.AddView(SettingsRow("Terminal", "Native PowerShell for Android · no container", () => { }, "0.2"));
        body.AddView(SettingsRow("Source", "github.com/mansfieldplumbing/terminal", () =>
            StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse("https://github.com/mansfieldplumbing/terminal")))));
        var dedication = SettingsText("In Loving Memory\nBillie Dean Mansfield", 12, "#8F96A3");
        dedication.SetPadding(SettingsDp(18), SettingsDp(22), SettingsDp(18), SettingsDp(8)); body.AddView(dedication);
    });

    private void RestoreSettingsDefaults() => new AlertDialog.Builder(this)
        .SetTitle("Restore visual defaults?")
        .SetMessage("This replaces settings.ps1 only. Your profile, scripts, files, and self-ADB identity are preserved.")
        .SetNegativeButton("Cancel", (_, _) => { })
        .SetPositiveButton("Restore", (_, _) =>
        {
            using var source = Assets!.Open("settings.ps1");
            using var target = System.IO.File.Create(_settingsPath); source.CopyTo(target);
            _settings = _session?.LoadSettings(_settingsPath) ?? new ConsoleSettings();
            TerminalSourcePolicy.DragonsEnabled = _settings.AllowDragons;
            ApplySettings();
        }).Show();

    private void OpenAppStorageSettings()
    {
        var intent = new Intent(Android.Provider.Settings.ActionApplicationDetailsSettings);
        intent.SetData(Android.Net.Uri.Parse("package:" + PackageName)); StartActivity(intent);
    }

    private void ShowSettingsMenuLegacy()
    {
        int Dp(int n) => (int)(n * Resources!.DisplayMetrics!.Density + .5f);
        TextView Label(string text, float size = 16, string color = "#F5F5F5")
        {
            var v = new TextView(this) { Text = text, TextSize = size };
            v.SetTextColor(Color.ParseColor(color));
            v.SetPadding(Dp(16), Dp(10), Dp(16), Dp(10));
            return v;
        }
        LinearLayout Card(string title)
        {
            var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
            var bg = new GradientDrawable(); bg.SetColor(Color.ParseColor("#262626"));
            bg.SetCornerRadius(Dp(10)); bg.SetStroke(Dp(1), Color.ParseColor("#454545"));
            card.Background = bg;
            card.AddView(Label(title, 14, "#9FD5FF"));
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            lp.SetMargins(Dp(16), Dp(7), Dp(16), Dp(7)); card.LayoutParameters = lp;
            return card;
        }
        Button Action(string text, Action action)
        {
            var b = new Button(this) { Text = text };
            b.SetAllCaps(false);
            b.SetTextColor(Color.White); b.Click += (_, _) => action();
            return b;
        }

        var body = new LinearLayout(this) { Orientation = Orientation.Vertical };
        body.SetPadding(0, Dp(8), 0, Dp(24));
        body.AddView(Label("Settings", 26));
        body.AddView(Label("Native PowerShell Console", 13, "#A0A0A0"));

        var appearance = Card("APPEARANCE");
        appearance.AddView(Label("Color theme", 17));
        appearance.AddView(Label("Applied immediately to the console and command field.", 13, "#A0A0A0"));
        var themes = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        themes.SetPadding(Dp(10), 0, Dp(10), Dp(10));
        foreach (var theme in new[] { ("PowerShell", "#012456", "#F5F5F5"), ("AMOLED", "#000000", "#F2F2F2"), ("Light", "#F4F4F4", "#171717") })
        {
            var button = Action(theme.Item1, () => { SetTheme(theme.Item2, theme.Item3); ApplySettings(); SaveSettings(); });
            themes.AddView(button, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        }
        appearance.AddView(themes); body.AddView(appearance);

        var terminal = Card("TERMINAL");
        var fontValue = Label($"{_settings.FontSize:0} px", 14, "#A0A0A0");
        var fontHeader = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        fontHeader.SetGravity(GravityFlags.CenterVertical);
        fontHeader.AddView(Label("Font size", 17), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        fontHeader.AddView(fontValue); terminal.AddView(fontHeader);
        terminal.AddView(Label("Pinch on the console or use this slider.", 13, "#A0A0A0"));
        var font = new SeekBar(this) { Max = 50, Progress = (int)_settings.FontSize - 14 };
        font.ProgressChanged += (_, e) => { if (!e.FromUser) return; _settings.FontSize = 14 + e.Progress; fontValue.Text = $"{_settings.FontSize:0} px"; ApplySettings(); SaveSettings(); };
        terminal.AddView(font);

        var scrollValue = Label($"{_settings.Scrollback:N0} lines", 14, "#A0A0A0");
        var scrollHeader = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        scrollHeader.SetGravity(GravityFlags.CenterVertical);
        scrollHeader.AddView(Label("Scrollback", 17), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        scrollHeader.AddView(scrollValue); terminal.AddView(scrollHeader);
        var scroll = new SeekBar(this) { Max = 199, Progress = Math.Clamp(_settings.Scrollback / 100 - 1, 0, 199) };
        scroll.ProgressChanged += (_, e) => { if (!e.FromUser) return; _settings.Scrollback = (e.Progress + 1) * 100; scrollValue.Text = $"{_settings.Scrollback:N0} lines"; SaveSettings(); };
        terminal.AddView(scroll); body.AddView(terminal);

        var sessions = Card("SESSIONS");
        sessions.AddView(Label("Protected background session", 17));
        sessions.AddView(Label("An ongoing notification prevents accidental swipe dismissal. Stop always asks for confirmation.", 13, "#A0A0A0"));
        sessions.AddView(Action(SessionGuardianService.IsRunning ? "Session guardian is running" : "Start session guardian  ›", () =>
        {
            AndroidBridge.StartSessionGuardian("PowerShell session", "Local CoreCLR runspace");
        }));
        sessions.AddView(Action("Open notification settings  ›", () =>
        {
            var intent = new Android.Content.Intent(Android.Provider.Settings.ActionAppNotificationSettings);
            intent.PutExtra(Android.Provider.Settings.ExtraAppPackage, PackageName);
            StartActivity(intent);
        }));
        body.AddView(sessions);

        var android = Card("ANDROID");
        android.AddView(Action("Privacy & permissions tour  ›", () => Oobe.Show(this)));
        android.AddView(Action("Set up self-ADB  ›", () => AdbLoopback.BeginSetup()));
        android.AddView(Action("Forget self-ADB pairing  ›", () => AdbLoopback.Forget()));
        android.AddView(Action("File manager access  ›", () => AndroidBridge.RequestFileManagerAccess()));
        android.AddView(Action("Show ADB status  ›", () => _console?.Append($"\n{AndroidBridge.GetAdbStatus()}\n")));
        android.AddView(Action("Show settings.ps1 path  ›", () => _console?.Append($"\n{_settingsPath}\n")));
        body.AddView(android);

        var about = Card("ABOUT");
        about.AddView(Label("Native PowerShell Console 0.2", 17));
        about.AddView(Label("Android-native CoreCLR + PowerShell\nCanvas presenter • no WebView • no container", 13, "#A0A0A0"));
        about.AddView(Label("Terminal  ›", 15, "#9FD5FF"));
        var sourceLink = Label("github.com/mansfieldplumbing/terminal", 13, "#8BD5FF");
        sourceLink.Clickable = true;
        sourceLink.Click += (_, _) => StartActivity(new Android.Content.Intent(
            Android.Content.Intent.ActionView,
            Android.Net.Uri.Parse("https://github.com/mansfieldplumbing/terminal")));
        about.AddView(sourceLink);
        about.AddView(Label("In Loving Memory", 11, "#909090"));
        about.AddView(Label("Billie Dean Mansfield", 11, "#808080"));
        body.AddView(about);

        var scrollView = new ScrollView(this); scrollView.AddView(body);
        var dialog = new AlertDialog.Builder(this)
            .SetView(scrollView)
            .SetNegativeButton("Done", (_, _) => { })
            .Create();
        dialog.Show();
        dialog.Window?.SetLayout(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
    }

    public bool EditFile(string path)
    {
        using var done = new ManualResetEventSlim(false);
        bool saved = false;
        RunOnUiThread(() =>
        {
            string fullPath = System.IO.Path.GetFullPath(path);
            string original = System.IO.File.Exists(fullPath) ? System.IO.File.ReadAllText(fullPath) : string.Empty;
            var editor = new EditText(this)
            {
                Text = original,
                TextSize = 16,
                Gravity = GravityFlags.Top | GravityFlags.Left,
                InputType = Android.Text.InputTypes.ClassText |
                            Android.Text.InputTypes.TextFlagMultiLine |
                            Android.Text.InputTypes.TextFlagNoSuggestions
            };
            editor.SetSingleLine(false);
            editor.SetHorizontallyScrolling(true);
            editor.SetPadding(24, 20, 24, 20);
            var frame = new FrameLayout(this);
            frame.SetPadding(16, 8, 16, 8);
            frame.AddView(editor, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
            new AlertDialog.Builder(this)
                .SetTitle(System.IO.Path.GetFileName(fullPath))
                .SetMessage(fullPath)
                .SetView(frame)
                .SetNegativeButton("Cancel", (_, _) => done.Set())
                .SetPositiveButton("Save", (_, _) =>
                {
                    string? directory = System.IO.Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(directory)) System.IO.Directory.CreateDirectory(directory);
                    string temporary = fullPath + ".tmp";
                    System.IO.File.WriteAllText(temporary, editor.Text ?? string.Empty);
                    System.IO.File.Move(temporary, fullPath, true);
                    saved = true;
                    done.Set();
                })
                .SetOnCancelListener(new EditorCancelListener(done))
                .Show();
            editor.RequestFocus();
        });
        done.Wait();
        return saved;
    }

    private sealed class EditorCancelListener(ManualResetEventSlim done) : Java.Lang.Object, IDialogInterfaceOnCancelListener
    {
        public void OnCancel(IDialogInterface? dialog) => done.Set();
    }

    private void SetTheme(string background, string foreground)
    {
        _settings.Background = background;
        _settings.Foreground = foreground;
        _settings.InputBackground = background;
        _settings.InputForeground = foreground;
    }

    private void ApplySettings()
    {
        _console?.SetFontSize(_settings.FontSize);
        _console?.SetScrollback(_settings.Scrollback);
        _console?.ApplyColors(_settings.Background, _settings.Foreground);
        if (_input != null) _input.TextSize = _settings.FontSize;
        _input?.SetTextColor(Color.ParseColor(_settings.InputForeground));
        _input?.SetBackgroundColor(Color.ParseColor(_settings.InputBackground));
    }

    private void SaveSettings()
    {
        string Q(string s) => s.Replace("'", "''");
        System.IO.File.WriteAllText(_settingsPath, $$"""
$NativeConsoleSettings = @{
    Background = '{{Q(_settings.Background)}}'
    Foreground = '{{Q(_settings.Foreground)}}'
    InputBackground = '{{Q(_settings.InputBackground)}}'
    InputForeground = '{{Q(_settings.InputForeground)}}'
    HintForeground = '{{Q(_settings.HintForeground)}}'
    FontSize = {{_settings.FontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}}
    Scrollback = {{_settings.Scrollback}}
    Prompt = '{{Q(_settings.Prompt)}}'
    AllowDragons = ${{_settings.AllowDragons.ToString().ToLowerInvariant()}}
    SettingsVersion = 4
}
""");
    }

    private async void OnEditorAction(object? sender, TextView.EditorActionEventArgs e)
    {
        if (e.ActionId != ImeAction.Send && e.Event?.KeyCode != Keycode.Enter) return;
        e.Handled = true;
        string command = _input?.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(command)) return;
        if (_input != null) _input.Text = string.Empty;
        if (_session == null) { _console?.Append("Runspace is still starting.\n"); return; }
        _console?.Append(_session.Highlight(command) + "\n");
        await _session.ExecuteAsync(command);
        _console?.Append(await _session.GetPromptAsync());
    }

    private string SeedSettings()
    {
        string path = System.IO.Path.Combine(FilesDir!.AbsolutePath, "settings.ps1");
        if (System.IO.File.Exists(path))
        {
            string existing = System.IO.File.ReadAllText(path);
            if (existing.Contains("SettingsVersion = 3"))
            {
                existing = existing.Replace("FontSize = 16", "FontSize = 14")
                    .Replace("SettingsVersion = 3", "SettingsVersion = 4");
                System.IO.File.WriteAllText(path, existing);
            }
            if (existing.Contains("SettingsVersion = 2"))
            {
                existing = existing.Replace("FontSize = 22", "FontSize = 14")
                    .Replace("SettingsVersion = 2", "SettingsVersion = 4");
                System.IO.File.WriteAllText(path, existing);
            }
            if (!existing.Contains("SettingsVersion") && existing.Contains("FontSize = 30"))
                System.IO.File.WriteAllText(path, existing.Replace("FontSize = 30", "FontSize = 14") + "\n$NativeConsoleSettings.SettingsVersion = 4\n");
            return path;
        }
        using var source = Assets!.Open("settings.ps1");
        using var target = System.IO.File.Create(path);
        source.CopyTo(target);
        return path;
    }

    private string SeedZoo()
    {
        string directory = System.IO.Path.Combine(FilesDir!.AbsolutePath, ".System");
        System.IO.Directory.CreateDirectory(directory);
        string[] scripts =
        {
            "ConvertFrom-DumpsysTree.ps1", "ConvertFrom-KeyValue.ps1",
            "ConvertFrom-Settings.ps1", "ConvertFrom-Table.ps1", "Test-Parsers.ps1"
        };
        foreach (string name in scripts)
        {
            string targetPath = System.IO.Path.Combine(directory, name);
            if (System.IO.File.Exists(targetPath)) continue;
            using var source = Assets!.Open("System/" + name);
            using var target = System.IO.File.Create(targetPath);
            source.CopyTo(target);
        }
        return directory;
    }

    protected override void OnDestroy()
    {
        if (_session != null) _session.Output -= OnSessionOutput;
        _session = null;
        base.OnDestroy();
    }
}
