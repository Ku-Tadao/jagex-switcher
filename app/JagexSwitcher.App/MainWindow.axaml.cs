using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
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
using System.Text.RegularExpressions;

namespace JagexSwitcher.App;

public partial class MainWindow : Window
{
    private readonly SwitcherService switcher;
    private static readonly Animation Enter = new()
    {
        Duration = TimeSpan.FromMilliseconds(220), Easing = new CubicEaseOut(),
        Children =
        {
            new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0d), new Setter(TranslateTransform.YProperty, 14d) } },
            new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1d), new Setter(TranslateTransform.YProperty, 0d) } }
        }
    };
    private static readonly string[] CaptureSteps = { "Sign in to Jagex Launcher", "Choose RuneLite and start your character", "We save the session automatically" };
    private readonly DispatcherTimer captureTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Border[] stepMarks = CaptureSteps.Select((_, i) => new Border { Classes = { "step" }, Child = new TextBlock { Text = (i + 1).ToString() } }).ToArray();
    private readonly TextBox profileName = new() { PlaceholderText = "Blank uses the character name" };
    private readonly TextBox renameName = new();
    private readonly TextBox pairUrl = new() { PlaceholderText = "https://….trycloudflare.com" };
    private readonly TextBox pairCode = new() { PlaceholderText = "8-digit code" };
    private readonly TextBox receiveName = new() { PlaceholderText = "Blank uses the sent name" };
    private readonly TextBlock captureStep = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13, Classes = { "gold" } };
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
    private bool advancedOpen;
    private int stableTicks;

    public MainWindow() : this(new SwitcherService()) { }

    internal MainWindow(SwitcherService switcher)
    {
        this.switcher = switcher;
        InitializeComponent();
        try { if (File.Exists(ThemeFile)) Application.Current!.RequestedThemeVariant = ParseTheme(File.ReadAllText(ThemeFile)); }
        catch (Exception ex) { Trace.TraceError("Could not read the saved theme: {0}", ex.Message); }
        toastTimer.Tick += (_, _) => { if (!busy) { toastTimer.Stop(); Toast.Opacity = 0; } };
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
        try
        {
            // Open on the most recently played profile so Enter resumes the last session.
            LoadProfiles(switcher.GetProfiles().Where(p => p.LastPlayed is not null).MaxBy(p => p.LastPlayed)?.Name);
            UpdateCapture();
        }
        catch (Exception ex) { ShowError(ex.Message); }
        Opened += (_, _) => ProfileList.Focus();
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
        ListEmpty.Text = profiles.Count == 0 ? "No profiles yet.\nAdd an account to get started." : $"Nothing matches \u201C{search}\u201D.\nPress Esc to clear the search.";
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var selected = Selected;
        SelectedDetails.IsVisible = selected is not null;
        WelcomePanel.IsVisible = profiles.Count == 0;
        if (selected is null) return;
        var name = string.IsNullOrWhiteSpace(selected.DisplayName) ? selected.Name : selected.DisplayName;
        if (CharacterName.Text != name) _ = Enter.RunAsync(SelectedDetails);
        CharacterName.Text = name;
        SelectedName.Text = selected.Name;
        CredentialText.Text = selected.IsReady ? "Ready to launch" : selected.CredentialStatus;
        StatusDot.Classes.Set("ready", selected.IsReady);
        PlayButton.IsEnabled = selected.IsReady;
        ReimportButton.IsVisible = !selected.IsReady;
        LastPlayedText.Text = selected.LastPlayed is { } played ? ProfileInfo.Ago(played) : "Not played yet";
        ToolTip.SetTip(LastPlayedText, FormatTime(selected.LastPlayedAt, null));
        ImportedText.Text = FormatTime(selected.ImportedAt, "Unknown", "dd MMM yyyy");
        ToolTip.SetTip(ImportedText, FormatTime(selected.ImportedAt, null));
    }

    private void ShowAdd()
    {
        ShowDialog("add", "Add an account", "Save a character once. Launch it from here every time after.", Stack(
            Field("Profile name (optional)", profileName),
            ActionButton("Open launcher & capture", StartCapture, "primary"),
            Note("Jagex Launcher opens. Sign in, choose RuneLite and start the character you want to save."),
            Divider(),
            Note("Already signed in? Add Current imports the login RuneLite last wrote to this PC."),
            ActionButton("Add Current", () => Import())));
        Dispatcher.UIThread.Post(() => profileName.Focus());
    }

    private void ShowManage()
    {
        if (Selected is not { } selected) return;
        renameName.Text = selected.Name;
        ShowDialog("manage", "Manage profile", selected.Name, Stack(
            Field("Profile name", renameName),
            ActionButton("Save name", Rename, "primary"),
            Divider(),
            Note("Re-import replaces this profile's saved session with the current RuneLite login."),
            Row(ActionButton("Re-import session", Reimport), ActionButton("Open vault folder", OpenVault)),
            Divider(),
            ActionButton("Remove profile", Remove, "danger")));
        Dispatcher.UIThread.Post(() => { renameName.Focus(); renameName.SelectAll(); });
    }

    private void ShowTransfer()
    {
        var content = Stack(Segmented(receiving ? 1 : 0, index => { receiving = index == 1; ShowTransfer(); }, "Send", "Receive"));
        if (receiving)
        {
            pairUrl.IsReadOnly = pairCode.IsReadOnly = false;
            content.Children.Add(Note("Enter the pair URL and code shown on the other PC. The session is encrypted for this Windows user after import."));
            content.Children.Add(ActionButton("Paste pair info", PastePair));
            content.Children.Add(Field("Pair URL", pairUrl));
            content.Children.Add(Field("Pair code", pairCode));
            content.Children.Add(Field("Import as (optional)", receiveName));
            content.Children.Add(ActionButton("Receive profile", Receive, "primary"));
        }
        else if (activeTransfer is not null)
        {
            // The live share owns its values; receive drafts are never used for Copy.
            var url = new TextBox { Text = activeTransfer.TunnelUrl, IsReadOnly = true };
            var code = new TextBox { Text = activeTransfer.Code, IsReadOnly = true, Classes = { "code" }, TextAlignment = TextAlignment.Center };
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
        var variant = Application.Current!.RequestedThemeVariant;
        var theme = Segmented(variant == ThemeVariant.Dark ? 1 : variant == ThemeVariant.Light ? 2 : 0, index =>
        {
            var name = index switch { 1 => "Dark", 2 => "Light", _ => "System" };
            Application.Current.RequestedThemeVariant = ParseTheme(name);
            try { Directory.CreateDirectory(switcher.GetVaultRoot()); File.WriteAllText(ThemeFile, name); }
            catch (Exception ex) { SetStatus($"Theme applied, but it could not be saved: {ex.Message}"); }
            ShowSettings();
        }, "System", "Dark", "Light");
        var content = Stack(
            Field("Appearance", theme),
            Divider(),
            Note("Profiles live in a Windows-encrypted vault on this PC. Nothing is uploaded."),
            ActionButton("Open vault folder", OpenVault),
            Divider(),
            Note("Shortcuts:  Enter play  ·  Ctrl+F search  ·  Ctrl+N add  ·  F2 manage  ·  Del remove  ·  F5 refresh"),
            Divider(),
            Button(advancedOpen ? "Hide advanced capture controls" : "Show advanced capture controls", () => { advancedOpen = !advancedOpen; ShowSettings(); }, "subtle"));
        if (advancedOpen)
        {
            var keepCapture = new CheckBox { Content = "Keep capture enabled when playing", IsChecked = advancedCapture };
            keepCapture.IsCheckedChanged += (_, _) => advancedCapture = keepCapture.IsChecked == true;
            content.Children.Add(Note("Capture can write live session credentials to disk. Normal Play turns it off. Only keep it enabled while troubleshooting."));
            content.Children.Add(keepCapture);
            content.Children.Add(Row(
                ActionButton("Prepare login", () => { SetStatus(switcher.PrepareLogin()); return Task.CompletedTask; }),
                ActionButton("Capture login", () => { switcher.StartCaptureLogin(); SetStatus("Capture enabled; Jagex Launcher opened."); return Task.CompletedTask; })));
            content.Children.Add(Row(
                ActionButton("Enable capture", () => { switcher.SetCaptureEnabled(true); SetStatus("Capture enabled."); return Task.CompletedTask; }),
                ActionButton("Disable capture", () => { switcher.SetCaptureEnabled(false); SetStatus("Capture disabled."); return Task.CompletedTask; })));
            content.Children.Add(ActionButton("Forget all profiles", ForgetAll, "danger"));
        }
        ShowDialog("settings", "Settings", "Appearance, local storage, and capture tools.", content);
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
        SetCaptureStage(0, "Opening Jagex Launcher…");
        ShowCapture();
        captureTimer.Start();
        SetStatus("In Jagex Launcher, choose RuneLite and start your character.");
    }

    private void ShowCapture()
    {
        var steps = new StackPanel { Spacing = 12 };
        for (var i = 0; i < CaptureSteps.Length; i++)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
            if (stepMarks[i].Parent is Panel parent) parent.Children.Remove(stepMarks[i]);
            var label = new TextBlock { Text = CaptureSteps[i], FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(label, 1);
            row.Children.Add(stepMarks[i]); row.Children.Add(label);
            steps.Children.Add(row);
        }
        ShowDialog("capture", "Waiting for your character", "Keep this window open while the session is captured.", Stack(
            steps, new ProgressBar { IsIndeterminate = true, Height = 3, MinHeight = 3, Margin = new Thickness(0, 6, 0, 0) },
            captureStep, ActionButton("Cancel capture", CancelCapture)));
    }

    private void SetCaptureStage(int stage, string text)
    {
        captureStep.Text = text;
        for (var i = 0; i < stepMarks.Length; i++)
        {
            stepMarks[i].Classes.Set("done", i < stage);
            stepMarks[i].Classes.Set("active", i == stage);
            ((TextBlock)stepMarks[i].Child!).Text = i < stage ? "\u2713" : (i + 1).ToString();
        }
    }

    private Task WatchCapture()
    {
        if (!captureActive || pendingProfileName is null) return Task.CompletedTask;
        if (switcher.IsOfficialOldSchoolClientRunning()) throw new InvalidOperationException("The official Old School client launched. Choose RuneLite in Jagex Launcher and try again.");
        var launcher = switcher.IsJagexLauncherRunning();
        var runeLite = switcher.IsRuneLiteRunning();
        if (runeLite && !launcher) throw new InvalidOperationException("RuneLite is running without Jagex Launcher. Start it through Jagex Launcher to capture a session.");
        if (!launcher) SetCaptureStage(0, "Waiting for Jagex Launcher…");
        else if (!runeLite) SetCaptureStage(1, "Launcher detected. Start your character in RuneLite.");
        else if (!switcher.HasCapturedCredentials())
        {
            stableTicks = 0;
            SetCaptureStage(2, "RuneLite detected. Waiting for the captured session…");
        }
        else if (++stableTicks < 2) SetCaptureStage(3, "Session found. Verifying…");
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

    private async Task PastePair()
    {
        var clipboard = Clipboard ?? throw new InvalidOperationException("The clipboard is unavailable. Type the pair URL and code instead.");
        var text = await clipboard.TryGetTextAsync() ?? "";
        var url = Regex.Match(text, @"https?://\S+");
        var code = Regex.Match(url.Success ? text.Replace(url.Value, "") : text, @"\b\d{8}\b");
        if (!url.Success && !code.Success) throw new InvalidOperationException("The clipboard has no pair info. Use Copy pair info on the sending PC first.");
        if (url.Success) pairUrl.Text = url.Value;
        if (code.Success) pairCode.Text = code.Value;
        SetStatus("Pair info pasted.");
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
        var cancel = Button("Cancel", () => result.TrySetResult(false));
        var confirm = Button(accept, () => result.TrySetResult(true), "danger");
        confirm.Classes.Add("filled");
        ShowDialog("confirm", title, message, Row(cancel, confirm));
        cancel.Focus();
        var confirmed = await result.Task;
        confirmation = null;
        if (closed) return false;
        if (previous.IsVisible) ShowDialog(previous.dialog, previous.Item2!, previous.Item3!, (Control)previous.Content!);
        else HideDialog();
        return confirmed;
    }

    private void ShowDialog(string kind, string title, string subtitle, Control content)
    {
        if (!DialogOverlay.IsVisible) _ = Enter.RunAsync(DialogCard);
        dialog = kind;
        DialogTitle.Text = title; DialogSubtitle.Text = subtitle;
        DialogContent.Content = content;
        DialogError.IsVisible = false;
        if (!DialogOverlay.IsVisible) Toast.Opacity = 0; // an older message should not hang over a fresh dialog
        DialogOverlay.IsVisible = true;
        Workspace.IsEnabled = false;
        Workspace.Effect = new BlurEffect { Radius = 12 };
        DialogClose.Focus();
    }

    private void HideDialog()
    {
        dialog = "";
        DialogOverlay.IsVisible = false;
        DialogContent.Content = null;
        Workspace.Effect = null;
        Workspace.IsEnabled = !busy;
        Dispatcher.UIThread.Post(() => { if (!closed && !DialogOverlay.IsVisible) ProfileList.Focus(); });
    }

    private void ShowError(string message)
    {
        // Form dialogs keep their inputs and show the problem inline so it can be corrected in place.
        if (DialogOverlay.IsVisible && dialog is "add" or "manage" or "transfer" or "settings")
        {
            DialogErrorText.Text = message;
            DialogError.IsVisible = true;
            return;
        }
        ShowDialog("error", "Couldn't finish that", message, Stack(Button("OK", HideDialog, "primary")));
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (busy || closed) return;
        busy = true;
        Workspace.IsEnabled = false;
        BusyIndicator.IsVisible = true;
        DialogError.IsVisible = false;
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
    private void SetStatus(string text)
    {
        StatusText.Text = text;
        // Over the artwork normally; below the dialog while one is open.
        Toast.VerticalAlignment = DialogOverlay.IsVisible ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        Toast.Margin = DialogOverlay.IsVisible ? new Thickness(24, 0, 24, 20) : new Thickness(336, 14, 160, 0);
        Toast.Opacity = 1;
        toastTimer.Stop(); toastTimer.Start();
    }

    private async void HandleKeys(object? sender, KeyEventArgs e)
    {
        var focused = FocusManager?.GetFocusedElement() as Control;
        if (DialogOverlay.IsVisible)
        {
            if (e.Key == Key.Escape) { CloseDialogClicked(this, new RoutedEventArgs()); e.Handled = true; }
            // Enter in a text field runs the dialog's primary action.
            else if (e.Key == Key.Enter && focused is TextBox && !busy
                && DialogCard.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("primary") && b.IsEffectivelyEnabled) is { } primary)
            {
                primary.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); e.Handled = true;
            }
            return;
        }
        if (busy || captureActive) return;
        var inSearch = focused == SearchBox;
        var inList = focused == ProfileList || focused?.GetVisualAncestors().Contains(ProfileList) == true;
        var ctrl = e.KeyModifiers == KeyModifiers.Control;
        switch (e.Key)
        {
            case Key.F5: await RunAsync(() => { LoadProfiles(); SetStatus("Profiles refreshed."); return Task.CompletedTask; }); break;
            case Key.F when ctrl: SearchBox.Focus(); SearchBox.SelectAll(); break;
            case Key.N when ctrl: ShowAdd(); break;
            case Key.Escape when inSearch && !string.IsNullOrEmpty(SearchBox.Text): SearchBox.Text = ""; break;
            // Type to filter, arrow to pick, Enter to play, all without leaving the search box.
            case Key.Down or Key.Up when inSearch && ProfileList.ItemCount > 0:
                ProfileList.SelectedIndex = Math.Clamp(ProfileList.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, ProfileList.ItemCount - 1); break;
            case Key.F2 when Selected is not null: ShowManage(); break;
            case Key.Delete when inList: await RunAsync(Remove); break;
            case Key.Enter when (inList || inSearch || focused == this || focused is null) && Selected is not null: await RunAsync(Play); break;
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
    private void OpenReceive(object? sender, RoutedEventArgs e) { receiving = true; ShowTransfer(); }
    private void ScrimPressed(object? sender, PointerPressedEventArgs e) { if (e.Source == DialogOverlay) CloseDialogClicked(sender, e); }
    private async void ReimportClicked(object? sender, RoutedEventArgs e) => await RunAsync(Reimport);
    private void SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateSelection();
    private void SearchChanged(object? sender, TextChangedEventArgs e) => ApplyFilter(Selected?.Name);
    private async void PlayClicked(object? sender, RoutedEventArgs e) => await RunAsync(Play);
    private async void PlayDoubleTapped(object? sender, TappedEventArgs e) => await RunAsync(Play);
    private void Minimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize(object? sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow(object? sender, RoutedEventArgs e) => Close();
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
    private static UniformGrid Row(params Control[] children)
    {
        var row = new UniformGrid { Rows = 1, Columns = children.Length, ColumnSpacing = 10 };
        row.Children.AddRange(children);
        return row;
    }
    private static Border Divider() => new() { Classes = { "divider" } };
    private static Border Segmented(int active, Action<int> select, params string[] labels)
    {
        var buttons = labels.Select((label, i) => Button(label, () => select(i))).ToArray();
        buttons[active].Classes.Add("active");
        return new Border { Classes = { "segmented" }, Child = Row(buttons) };
    }
    private string ThemeFile => Path.Combine(switcher.GetVaultRoot(), "ui-theme.txt");
    private static ThemeVariant ParseTheme(string name) => name.Trim() switch { "Dark" => ThemeVariant.Dark, "Light" => ThemeVariant.Light, _ => ThemeVariant.Default };
    private static TextBlock Note(string text) => new() { Text = text, Classes = { "quiet" }, FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
    private static Control Field(string label, Control input)
    {
        Avalonia.Automation.AutomationProperties.SetName(input, label);
        return Stack(new TextBlock { Text = label, FontSize = 12, Classes = { "quiet" } }, input);
    }
    private static string? FormatTime(string value, string? empty, string format = "dd MMM yyyy · HH:mm") =>
        DateTimeOffset.TryParse(value, out var time) ? time.ToLocalTime().ToString(format) : empty;

    private void PlatformColorsChanged(object? sender, PlatformColorValues values) =>
        ApplyHighContrast(values.ContrastPreference == ColorContrastPreference.High);

    internal void ApplyHighContrast(bool enabled)
    {
        Classes.Set("highcontrast", enabled);
        LandscapeImage.IsVisible = !enabled;
        foreach (var (key, systemColor) in new[] {
            ("CanvasBrush", 5), ("PanelBrush", 5), ("RaisedBrush", 15), ("HoverBrush", 15), ("LineBrush", 8),
            ("SelectionLineBrush", 13), ("DangerFillBrush", 5),
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
