using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace JagexSwitcher.App;

public partial class MainWindow : Window
{
    private readonly SwitcherService switcher;
    private readonly DispatcherTimer captureTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBox profileName = new() { PlaceholderText = "Blank uses the character name" };
    private readonly TextBox renameName = new();
    private readonly TextBox pairUrl = new() { PlaceholderText = "https://….trycloudflare.com" };
    private readonly TextBox pairCode = new() { PlaceholderText = "8-digit code" };
    private readonly TextBox receiveName = new() { PlaceholderText = "Blank uses the sent name" };
    private readonly TextBlock captureStep = new() { TextWrapping = TextWrapping.Wrap };
    private IReadOnlyList<ProfileInfo> profiles = Array.Empty<ProfileInfo>();
    private SwitcherService.PairTransferSession? activeTransfer;
    private TaskCompletionSource<bool>? confirmation;
    private string? pendingProfileName;
    private string dialog = "";
    private bool busy;
    private bool closed;
    private bool captureActive;
    private bool advancedCapture;
    private bool receiving;
    private int stableTicks;

    public MainWindow() : this(new SwitcherService()) { }

    internal MainWindow(SwitcherService switcher)
    {
        this.switcher = switcher;
        InitializeComponent();
        SizeChanged += (_, _) => HeroPanel.Height = Math.Clamp(Bounds.Height - 496, 160, 320);
        if (Application.Current?.PlatformSettings is { } platform)
        {
            ApplyHighContrast(platform.GetColorValues().ContrastPreference == ColorContrastPreference.High);
            platform.ColorValuesChanged += PlatformColorsChanged;
        }
        captureTimer.Tick += async (_, _) => await RunAsync(WatchCapture);
        AddHandler(KeyDownEvent, HandleKeys, RoutingStrategies.Tunnel);
        ProfileList.ContextMenu = new ContextMenu
        {
            ItemsSource = new[]
            {
                Menu("Play in RuneLite", () => Play()),
                Menu("Rename / manage", () => { ShowManage(); return Task.CompletedTask; }),
                Menu("Re-import", Reimport),
                Menu("Remove", Remove)
            }
        };
        ProfileList.ContextMenu.Opening += (_, e) => e.Cancel = Selected is null || busy || captureActive;
        Closing += (_, e) =>
        {
            // Keep credential writes and transfer startup alive until their result is handled.
            if (busy && confirmation is null)
            {
                e.Cancel = true;
                SetStatus("Wait for the current operation to finish before closing.");
            }
        };
        Closed += (_, _) =>
        {
            closed = true;
            if (Application.Current?.PlatformSettings is { } platform) platform.ColorValuesChanged -= PlatformColorsChanged;
            captureTimer.Stop();
            activeTransfer?.Dispose();
            confirmation?.TrySetResult(false);
            if (captureActive)
            {
                try { switcher.SetCaptureEnabled(false); }
                catch (Exception ex) { Trace.TraceError("Could not disable capture on close: {0}", ex.Message); }
            }
        };
        try { LoadProfiles(); UpdateCapture(); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private ProfileInfo? Selected => ProfileList.SelectedItem as ProfileInfo;

    private void LoadProfiles(string? selectName = null)
    {
        var previous = selectName ?? Selected?.Name;
        profiles = switcher.GetProfiles();
        ProfileCount.Text = profiles.Count.ToString();
        if (selectName is not null) SearchBox.Text = "";
        ApplyFilter(previous);
    }

    private void ApplyFilter(string? selectName = null)
    {
        var search = SearchBox.Text?.Trim() ?? "";
        var matches = profiles.Where(p => p.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || p.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        ProfileList.ItemsSource = matches;
        ProfileList.SelectedItem = matches.FirstOrDefault(p => string.Equals(p.Name, selectName, StringComparison.OrdinalIgnoreCase))
            ?? matches.FirstOrDefault();
        ListEmpty.IsVisible = matches.Count == 0;
        ListEmpty.Text = profiles.Count == 0 ? "No profiles yet.\nAdd an account to get started." : "No matching profiles.\nTry another name.";
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var selected = Selected;
        SelectedDetails.IsVisible = selected is not null;
        WelcomePanel.IsVisible = selected is null;
        CharacterName.Text = selected is null ? "Make yourself at home." :
            string.IsNullOrWhiteSpace(selected.DisplayName) ? selected.Name : selected.DisplayName;
        HeroEyebrow.Text = selected is null ? "A NEW ADVENTURE AWAITS" : "SELECTED CHARACTER";
        HeroCaption.Text = selected is null ? "Your characters. One place to launch." : "Old School RuneScape  /  RuneLite";
        if (selected is null) return;
        SelectedName.Text = selected.Name;
        CredentialText.Text = selected.IsReady ? "Saved session ready to launch" : $"{selected.CredentialStatus} credentials. Re-import this profile to play.";
        CredentialText.Classes.Set("ready", selected.IsReady);
        CredentialText.Classes.Set("danger", !selected.IsReady);
        PlayButton.IsEnabled = selected.IsReady;
        LastPlayedText.Text = FormatTime(selected.LastPlayedAt, "Not played yet");
        ImportedText.Text = FormatTime(selected.ImportedAt, "Unknown");
    }

    private void ShowAdd()
    {
        var content = Stack(
            Note("Sign in through Jagex Launcher, choose RuneLite, then start the character you want to save."),
            Field("Profile name (optional)", profileName),
            ActionButton("Open launcher & capture", StartCapture, "primary"),
            Note("Already captured a login? Add Current imports valid credentials that RuneLite has written to this PC."),
            ActionButton("Add Current", () => Import()));
        ShowDialog("add", "Add an account", "Save a character once. Launch it here next time.", content);
        Dispatcher.UIThread.Post(() => profileName.Focus());
    }

    private void ShowManage()
    {
        if (Selected is not { } selected) return;
        renameName.Text = selected.Name;
        ShowDialog("manage", "Manage profile", selected.Name, Stack(
            Field("Profile name", renameName),
            ActionButton("Save name", Rename, "primary"),
            Note("Re-import replaces this profile's saved session with the current captured RuneLite login."),
            ActionButton("Re-import current session", Reimport),
            ActionButton("Open vault folder", OpenVault),
            ActionButton("Remove profile", Remove, "danger")));
        Dispatcher.UIThread.Post(() => { renameName.Focus(); renameName.SelectAll(); });
    }

    private void ShowTransfer()
    {
        var send = Button("Send", () => { receiving = false; ShowTransfer(); });
        var receive = Button("Receive", () => { receiving = true; ShowTransfer(); });
        (receiving ? receive : send).Classes.Add("active");
        var mode = new UniformGrid { Columns = 2, Rows = 1, ColumnSpacing = 8 };
        mode.Children.Add(send); mode.Children.Add(receive);
        var content = Stack(mode);
        if (receiving)
        {
            pairUrl.IsReadOnly = pairCode.IsReadOnly = false;
            content.Children.Add(Note("Paste the pair URL and code from the other PC. The saved session is encrypted for this Windows user after import."));
            content.Children.Add(Field("Pair URL", pairUrl));
            content.Children.Add(Field("Pair code", pairCode));
            content.Children.Add(Field("Import as (optional)", receiveName));
            content.Children.Add(ActionButton("Receive profile", Receive, "primary"));
        }
        else if (activeTransfer is not null)
        {
            // The live share owns its values; receive drafts are never used for Copy.
            var url = new TextBox { Text = activeTransfer.TunnelUrl, IsReadOnly = true };
            var code = new TextBox { Text = activeTransfer.Code, IsReadOnly = true };
            content.Children.Add(Note("Share these details with your other PC. One import only; the share expires in 15 minutes. Keep this app open."));
            content.Children.Add(Field("Pair URL", url));
            content.Children.Add(Field("Pair code", code));
            content.Children.Add(ActionButton("Copy pair info", CopyPair, "primary"));
            content.Children.Add(ActionButton("Stop sharing", () => { StopTransfer("Pair transfer stopped."); return Task.CompletedTask; }, "danger"));
        }
        else
        {
            content.Children.Add(Note(Selected is null ? "Select a saved profile first, or choose Receive to import one from another PC." :
                $"Send '{Selected.Name}' to another PC you control. The share closes after one import, five failed code attempts, or 15 minutes."));
            var create = ActionButton("Create secure pair", StartTransfer, "primary");
            create.IsEnabled = Selected is not null;
            content.Children.Add(create);
        }
        ShowDialog("transfer", "Move between PCs", "A one-time transfer of a saved session.", content);
    }

    private void ShowSettings()
    {
        var theme = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "Follow Windows", "Dark", "Light" },
            SelectedIndex = Application.Current!.RequestedThemeVariant == ThemeVariant.Dark ? 1 :
                Application.Current.RequestedThemeVariant == ThemeVariant.Light ? 2 : 0 };
        theme.SelectionChanged += (_, _) => Application.Current!.RequestedThemeVariant = theme.SelectedIndex switch
            { 1 => ThemeVariant.Dark, 2 => ThemeVariant.Light, _ => ThemeVariant.Default };
        var keepCapture = new CheckBox { Content = "Keep capture enabled when playing", IsChecked = advancedCapture };
        keepCapture.IsCheckedChanged += (_, _) => advancedCapture = keepCapture.IsChecked == true;
        var advanced = Stack(
            Note("Capture can write live session credentials to disk. Normal Play turns it off. Only keep it enabled while troubleshooting."),
            keepCapture,
            ActionButton("Prepare login", () => { SetStatus(switcher.PrepareLogin()); return Task.CompletedTask; }),
            ActionButton("Capture login", () => { switcher.StartCaptureLogin(); SetStatus("Capture enabled; Jagex Launcher opened."); return Task.CompletedTask; }),
            ActionButton("Enable capture", () => { switcher.SetCaptureEnabled(true); SetStatus("Capture enabled."); return Task.CompletedTask; }),
            ActionButton("Disable capture", () => { switcher.SetCaptureEnabled(false); SetStatus("Capture disabled."); return Task.CompletedTask; }),
            ActionButton("Forget all profiles", ForgetAll, "danger"));
        ShowDialog("settings", "Settings", "Appearance, local storage, and capture tools.", Stack(
            Field("Appearance (this session)", theme),
            Note("Profiles stay in your existing Windows-encrypted vault. No account migration is needed."),
            ActionButton("Open vault folder", OpenVault),
            new Expander { Header = "Advanced capture controls", Content = advanced, HorizontalAlignment = HorizontalAlignment.Stretch }));
    }

    private async Task Import(string? forcedName = null)
    {
        var name = (forcedName ?? profileName.Text ?? "").Trim();
        if (!await ConfirmOverwrite(name)) return;
        var imported = switcher.Import(name);
        profileName.Text = "";
        LoadProfiles(imported);
        HideDialog();
        SetStatus($"Saved '{imported}'. Ready to play.");
    }

    private Task Reimport() => Selected is { } selected ? Import(selected.Name) : Task.CompletedTask;

    private async Task StartCapture()
    {
        var name = profileName.Text?.Trim() ?? "";
        if (!await ConfirmOverwrite(name)) return;
        if (switcher.IsRuneLiteRunning()) throw new InvalidOperationException("Close RuneLite before adding an account so the new Jagex-launched client can be tracked.");
        if (switcher.IsOfficialOldSchoolClientRunning()) throw new InvalidOperationException("Close the official Old School client first. Capture needs RuneLite.");
        var captureWasEnabled = switcher.IsCaptureEnabled();
        try { switcher.BeginAddAccount(); }
        catch { switcher.SetCaptureEnabled(captureWasEnabled); throw; }
        pendingProfileName = name;
        stableTicks = 0;
        captureActive = true;
        captureStep.Text = "Opening Jagex Launcher…";
        ShowCapture();
        captureTimer.Start();
        SetStatus("In Jagex Launcher, choose RuneLite and start your character.");
    }

    private void ShowCapture() => ShowDialog("capture", "Waiting for your character", "Keep this window open while the session is captured.", Stack(
        Note("1. Choose RuneLite in Jagex Launcher.\n2. Start the character you want to save.\n3. We'll capture and save the session automatically."),
        new ProgressBar { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 8) },
        captureStep, ActionButton("Cancel capture", CancelCapture)));

    private Task WatchCapture()
    {
        if (!captureActive || pendingProfileName is null) return Task.CompletedTask;
        if (switcher.IsOfficialOldSchoolClientRunning()) throw new InvalidOperationException("The official Old School client launched. Choose RuneLite in Jagex Launcher and try again.");
        var launcher = switcher.IsJagexLauncherRunning();
        var runeLite = switcher.IsRuneLiteRunning();
        if (runeLite && !launcher) throw new InvalidOperationException("RuneLite is running without Jagex Launcher. Start it through Jagex Launcher to capture a session.");
        if (!launcher) captureStep.Text = "Waiting for Jagex Launcher…";
        else if (!runeLite) captureStep.Text = "Launcher detected. Start your character in RuneLite.";
        else if (!switcher.HasCapturedCredentials())
        {
            stableTicks = 0;
            captureStep.Text = "RuneLite detected. Waiting for the captured session…";
        }
        else if (++stableTicks < 2) captureStep.Text = "Session found. Verifying…";
        else
        {
            var name = switcher.CompleteAddAccount(pendingProfileName);
            EndCapture();
            profileName.Text = "";
            LoadProfiles(name);
            HideDialog();
            SetStatus($"Saved '{name}'. Capture is now off.");
        }
        return Task.CompletedTask;
    }

    private void EndCapture()
    {
        captureTimer.Stop();
        captureActive = false;
        CaptureBadge.IsVisible = false;
        pendingProfileName = null;
        stableTicks = 0;
    }

    private Task CancelCapture()
    {
        switcher.SetCaptureEnabled(false);
        EndCapture();
        HideDialog();
        SetStatus("Capture cancelled and turned off.");
        return Task.CompletedTask;
    }

    private Task Play()
    {
        if (Selected is not { } selected) return Task.CompletedTask;
        if (!selected.IsReady) throw new InvalidOperationException("Re-import this profile before playing; its saved credentials are not ready.");
        if (!advancedCapture && switcher.IsCaptureEnabled()) switcher.SetCaptureEnabled(false);
        switcher.Play(selected.Name);
        LoadProfiles(selected.Name);
        SetStatus($"Launched '{selected.Name}' in RuneLite.");
        return Task.CompletedTask;
    }

    private Task Rename()
    {
        if (Selected is not { } selected) return Task.CompletedTask;
        var name = renameName.Text?.Trim() ?? "";
        switcher.RenameProfile(selected.Name, name);
        LoadProfiles(name);
        HideDialog();
        SetStatus($"Renamed profile to '{name}'.");
        return Task.CompletedTask;
    }

    private async Task Remove()
    {
        if (Selected is not { } selected || !await Confirm("Remove profile?", $"Remove '{selected.Name}' and its saved credentials from this PC?", "Remove profile")) return;
        switcher.Remove(selected.Name);
        LoadProfiles(); HideDialog();
        SetStatus($"Removed '{selected.Name}'.");
    }

    private async Task ForgetAll()
    {
        if (!await Confirm("Forget all profiles?", "This deletes every saved profile and its credentials from your local vault. This cannot be undone.", "Forget all profiles")) return;
        activeTransfer?.Dispose(); activeTransfer = null;
        switcher.ForgetAllProfiles();
        profileName.Text = renameName.Text = pairUrl.Text = pairCode.Text = receiveName.Text = "";
        LoadProfiles(); HideDialog(); SetStatus("All local profiles removed.");
    }

    private Task OpenVault()
    {
        var path = switcher.GetVaultRoot();
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        SetStatus("Opened vault folder.");
        return Task.CompletedTask;
    }

    private async Task StartTransfer()
    {
        if (Selected is not { } selected) return;
        activeTransfer?.Dispose(); activeTransfer = null;
        SetStatus("Preparing a secure share. First use downloads Cloudflare Tunnel…");
        var session = await switcher.StartPairTransferAsync(selected.Name);
        if (closed) { session.Dispose(); return; }
        activeTransfer = session;
        session.Closed += reason => Dispatcher.UIThread.Post(() =>
        {
            if (!closed && activeTransfer == session) StopTransfer(reason);
        });
        receiving = false;
        ShowTransfer();
        await CopyPair();
    }

    private async Task CopyPair()
    {
        if (activeTransfer is not { } session) throw new InvalidOperationException("No active share to copy.");
        var clipboard = Clipboard ?? throw new InvalidOperationException("The clipboard is unavailable. Copy the pair URL and code manually.");
        await clipboard.SetTextAsync($"Pair URL: {session.TunnelUrl}{Environment.NewLine}Pair code: {session.Code}");
        SetStatus("Pair info copied. Keep the app open until the other PC imports it.");
    }

    private void StopTransfer(string message)
    {
        activeTransfer?.Dispose(); activeTransfer = null;
        if (dialog == "transfer") ShowTransfer();
        SetStatus(message);
    }

    private async Task Receive()
    {
        SetStatus("Contacting the sending PC…");
        var name = await switcher.ReceivePairAsync(pairUrl.Text?.Trim() ?? "", pairCode.Text?.Trim() ?? "", receiveName.Text?.Trim() ?? "");
        pairUrl.Text = pairCode.Text = receiveName.Text = "";
        LoadProfiles(name); HideDialog();
        SetStatus($"Received '{name}'. Saved securely on this PC.");
    }

    private Task<bool> ConfirmOverwrite(string name) => profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
        ? Confirm("Replace saved session?", $"Replace the credentials saved for '{name}'?", "Replace session") : Task.FromResult(true);

    private async Task<bool> Confirm(string title, string message, string accept)
    {
        var previous = (dialog, DialogTitle.Text, DialogSubtitle.Text, DialogContent.Content, DialogOverlay.IsVisible);
        confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = confirmation;
        ShowDialog("confirm", title, message, Stack(
            Button(accept, () => result.TrySetResult(true), "danger"), Button("Cancel", () => result.TrySetResult(false))));
        var confirmed = await result.Task;
        confirmation = null;
        if (closed) return false;
        if (previous.IsVisible) ShowDialog(previous.dialog, previous.Item2!, previous.Item3!, (Control)previous.Content!);
        else HideDialog();
        return confirmed;
    }

    private void ShowDialog(string kind, string title, string subtitle, Control content)
    {
        dialog = kind;
        DialogTitle.Text = title; DialogSubtitle.Text = subtitle;
        DialogContent.Content = content;
        DialogOverlay.IsVisible = true;
        Workspace.IsEnabled = false;
        DialogClose.Focus();
    }

    private void HideDialog()
    {
        dialog = "";
        DialogOverlay.IsVisible = false;
        DialogContent.Content = null;
        Workspace.IsEnabled = !busy;
        Dispatcher.UIThread.Post(() => { if (!closed && !DialogOverlay.IsVisible) ProfileList.Focus(); });
    }

    private void ShowError(string message)
    {
        SetStatus(message);
        // Retain input controls so a failed import or transfer can be corrected.
        var previous = (dialog, DialogTitle.Text, DialogSubtitle.Text, DialogContent.Content, DialogOverlay.IsVisible);
        ShowDialog("error", "Couldn't finish that", message, Stack(Button("Back", () =>
        {
            if (previous.IsVisible && previous.dialog != "capture")
                ShowDialog(previous.dialog, previous.Item2!, previous.Item3!, (Control)previous.Content!);
            else HideDialog();
        }, "primary")));
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (busy || closed) return;
        busy = true;
        Workspace.IsEnabled = false;
        BusyIndicator.IsVisible = true;
        try { await action(); UpdateCapture(); }
        catch (Exception ex)
        {
            if (captureActive)
            {
                captureTimer.Stop();
                try { switcher.SetCaptureEnabled(false); EndCapture(); }
                catch (Exception cleanup) { ShowError($"{ex.Message}\nCapture could not be disabled: {cleanup.Message}"); return; }
            }
            ShowError(ex.Message);
        }
        finally
        {
            busy = false;
            BusyIndicator.IsVisible = false;
            Workspace.IsEnabled = !DialogOverlay.IsVisible;
        }
    }

    private void UpdateCapture() => CaptureBadge.IsVisible = switcher.IsCaptureEnabled();
    private void SetStatus(string text) { StatusText.Text = text; ToolTip.SetTip(StatusText, text); }

    private async void HandleKeys(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DialogOverlay.IsVisible)
        {
            CloseDialogClicked(this, new RoutedEventArgs()); e.Handled = true; return;
        }
        if (busy || DialogOverlay.IsVisible || captureActive) return;
        var focused = FocusManager?.GetFocusedElement() as Control;
        var inList = focused == ProfileList || focused?.GetVisualAncestors().Contains(ProfileList) == true;
        switch (e.Key)
        {
            case Key.F5: await RunAsync(() => { LoadProfiles(); SetStatus("Profiles refreshed."); return Task.CompletedTask; }); break;
            case Key.F2 when Selected is not null: ShowManage(); break;
            case Key.Delete when inList: await RunAsync(Remove); break;
            case Key.Enter when (inList || focused == this || focused is null) && Selected is not null: await RunAsync(Play); break;
            default: return;
        }
        e.Handled = true;
    }

    private async void CloseDialogClicked(object? sender, RoutedEventArgs e)
    {
        if (confirmation is not null) { confirmation.TrySetResult(false); return; }
        if (busy) return;
        if (captureActive) await RunAsync(CancelCapture);
        else HideDialog();
    }

    private void OpenAdd(object? sender, RoutedEventArgs e) => ShowAdd();
    private void OpenManage(object? sender, RoutedEventArgs e) => ShowManage();
    private void OpenTransfer(object? sender, RoutedEventArgs e) => ShowTransfer();
    private void OpenSettings(object? sender, RoutedEventArgs e) => ShowSettings();
    private void FocusProfiles(object? sender, RoutedEventArgs e) => ProfileList.Focus();
    private void SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateSelection();
    private void SearchChanged(object? sender, TextChangedEventArgs e) => ApplyFilter(Selected?.Name);
    private async void RefreshClicked(object? sender, RoutedEventArgs e) => await RunAsync(() => { LoadProfiles(); SetStatus("Profiles refreshed."); return Task.CompletedTask; });
    private async void PlayClicked(object? sender, RoutedEventArgs e) => await RunAsync(Play);
    private async void PlayDoubleTapped(object? sender, TappedEventArgs e) => await RunAsync(Play);
    private void Minimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize(object? sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow(object? sender, RoutedEventArgs e) => Close();
    private void DragWindow(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button || e.Source is Control control && control.GetVisualAncestors().OfType<Button>().Any()) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (e.ClickCount == 2) Maximize(sender, e);
            else BeginMoveDrag(e);
        }
    }

    private Button ActionButton(string label, Func<Task> action, string style = "") => Button(label, async () => await RunAsync(action), style);
    private static Button Button(string label, Action action, string style = "")
    {
        var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (style.Length > 0) button.Classes.Add(style);
        button.Click += (_, _) => action();
        return button;
    }
    private MenuItem Menu(string label, Func<Task> action)
    {
        var menu = new MenuItem { Header = label };
        menu.Click += async (_, _) => await RunAsync(action);
        return menu;
    }
    private static StackPanel Stack(params Control[] children)
    {
        var stack = new StackPanel { Spacing = 14 };
        foreach (var child in children)
        {
            // Reused fields and capture status retain state across dialog openings.
            if (child.Parent is Panel parent) parent.Children.Remove(child);
            stack.Children.Add(child);
        }
        return stack;
    }
    private static TextBlock Note(string text) => new() { Text = text, Classes = { "quiet" }, FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
    private static Control Field(string label, Control input)
    {
        Avalonia.Automation.AutomationProperties.SetName(input, label);
        return Stack(new TextBlock { Text = label, FontSize = 12, Classes = { "quiet" } }, input);
    }
    private static string FormatTime(string value, string empty) => DateTimeOffset.TryParse(value, out var time)
        ? time.ToLocalTime().ToString("dd MMM yyyy · HH:mm") : empty;

    private void PlatformColorsChanged(object? sender, PlatformColorValues values) =>
        ApplyHighContrast(values.ContrastPreference == ColorContrastPreference.High);

    internal void ApplyHighContrast(bool enabled)
    {
        Classes.Set("highcontrast", enabled);
        LandscapeImage.IsVisible = !enabled;
        foreach (var (key, systemColor) in new[] {
            ("CanvasBrush", 5), ("PanelBrush", 5), ("RaisedBrush", 15), ("LineBrush", 8),
            ("InkBrush", 8), ("QuietBrush", 8), ("GoldBrush", 13), ("SelectionBrush", 13),
            ("DangerBrush", 8), ("ReadyBrush", 8), ("PrimaryBrush", 13), ("PrimaryInkBrush", 14) })
        {
            if (!enabled) { Resources.Remove(key); continue; }
            var color = GetSysColor(systemColor);
            Resources[key] = new SolidColorBrush(Color.FromRgb((byte)color, (byte)(color >> 8), (byte)(color >> 16)));
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);
}
