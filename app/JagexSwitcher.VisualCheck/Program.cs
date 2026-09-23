using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JagexSwitcher.App;
using System.Reflection;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// dotnet run --project app/JagexSwitcher.VisualCheck -- .local/avalonia-check
// Real DPAPI and service operations, synthetic sessions, isolated filesystem paths.
internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main(string[] args)
    {
        try { Run(args); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Run(string[] args)
    {
        SwitcherService.SelfCheck();
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        var output = Path.GetFullPath(args.FirstOrDefault() ?? ".local/avalonia-check");
        Directory.CreateDirectory(output);
        var isolated = Path.Combine(output, "synthetic-vault-" + Guid.NewGuid());
        Directory.CreateDirectory(isolated);
        var service = new SwitcherService();
        foreach (var name in new[] { "vaultRoot", "liveCreds", "runeLiteExe", "runeLiteSettings", "jagexLauncherExe" })
            typeof(SwitcherService).GetField(name, Private)!.SetValue(service, Path.Combine(isolated, name));

        var window = new MainWindow(service);
        window.Show();
        Pump();
        Capture("empty");
        Click("AddButton");
        Field<TextBox>("profileName").Text = "My new account";
        Capture("add");
        Escape();
        Click("AddButton");
        Check(Field<TextBox>("profileName").Text == "My new account", "Account draft was lost.");
        Field<TextBox>("profileName").Text = "Main";
        File.WriteAllText(Path.Combine(isolated, "liveCreds"), Credentials("Oak & Ember"));
        Click("Add Current");
        Wait(() => !Find<Border>("DialogOverlay").IsVisible);
        Check(service.GetProfiles().Count == 1, "Add Current did not save the profile.");
        Seed("Ironman", "Iron Tadao");
        Seed("Pure", "Wildy scout");
        Seed("Long_profile_name_to_test_truncation", "A long character name");
        File.Delete(Path.Combine(isolated, "vaultRoot", "credentials", "Pure.properties"));
        SendKey(Key.F5);
        var list = Find<ListBox>("ProfileList");
        list.SelectedItem = list.Items.Cast<ProfileInfo>().Single(p => p.Name == "Main");
        Capture("profiles");
        Check(Find<TextBlock>("CharacterName").Text == "Oak & Ember", "Selected character not shown.");
        Check(Find<Button>("PlayButton").IsEnabled, "Ready profile cannot be played.");
        Find<Button>("AddButton").Focus();
        SendKey(Key.Enter);
        Check(Find<TextBlock>("DialogTitle").Text == "Add an account", "Enter on Add account was intercepted by Play.");
        Escape();
        Find<TextBox>("SearchBox").Text = "wildy";
        Pump();
        Check(list.ItemCount == 1 && !Find<Button>("PlayButton").IsEnabled, "Search or missing-credential state failed.");
        Capture("missing-credentials");
        Find<TextBox>("SearchBox").Text = "No such player";
        Pump();
        Check(list.ItemCount == 0 && Find<TextBlock>("ListEmpty").IsVisible, "No-results state is missing.");
        Find<TextBox>("SearchBox").Text = "";
        Pump();
        list.SelectedItem = list.Items.Cast<ProfileInfo>().Single(p => p.Name == "Main");

        SendKey(Key.F2);
        Check(Field<TextBox>("renameName").IsFocused, "F2 did not focus rename.");
        Field<TextBox>("renameName").Text = "Main_renamed";
        Click("Save name");
        Wait(() => !Find<Border>("DialogOverlay").IsVisible);
        Check(service.GetProfiles().Any(p => p.Name == "Main_renamed"), "Rename did not reach the service.");

        Click("ManageButton"); Click("Remove profile");
        Wait(() => Find<TextBlock>("DialogTitle").Text == "Remove profile?");
        Capture("confirm-remove");
        Click("Cancel");
        Wait(() => Find<TextBlock>("DialogTitle").Text == "Manage profile");
        Check(service.GetProfiles().Count == 4, "Cancelled removal changed the vault.");
        Click("Remove profile");
        Wait(() => Find<TextBlock>("DialogTitle").Text == "Remove profile?");
        Click("Remove profile");
        Wait(() => !Find<Border>("DialogOverlay").IsVisible);
        Check(service.GetProfiles().Count == 3, "Confirmed removal failed.");

        Click("TransferButton"); Click("Receive");
        Field<TextBox>("pairUrl").Text = "http://example.invalid";
        Field<TextBox>("pairCode").Text = "12345678";
        Capture("receive");
        Click("Receive profile");
        Wait(() => Find<TextBlock>("DialogTitle").Text == "Couldn't finish that");
        Capture("transfer-error");
        Click("Back");
        Check(Field<TextBox>("pairCode").Text == "12345678", "Error discarded transfer draft.");
        Escape(); Click("TransferButton");
        Check(Field<TextBox>("pairUrl").Text == "http://example.invalid", "Reopening transfer discarded draft.");

        // Complete a real receive request locally; no Cloudflare or real sessions involved.
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var responseTask = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var reader = new StreamReader(request.Request.InputStream);
            Check((await reader.ReadToEndAsync()).Contains("12345678"), "Pair code was not sent.");
            var body = JsonSerializer.SerializeToUtf8Bytes(new { profileName = "Transferred", displayName = "Travel Tadao", credentials = Credentials("Travel Tadao") });
            request.Response.ContentType = "application/json";
            await request.Response.OutputStream.WriteAsync(body);
            request.Response.Close();
        });
        Field<TextBox>("pairUrl").Text = $"http://127.0.0.1:{port}";
        Field<TextBox>("receiveName").Text = "Transferred";
        Click("Receive profile");
        Wait(() => !Find<Border>("DialogOverlay").IsVisible);
        responseTask.GetAwaiter().GetResult();
        Check(service.GetProfiles().Any(p => p.Name == "Transferred" && p.IsReady), "Receive did not save encrypted credentials.");

        // Verify the send panel copies the live session rather than a receive draft.
        using var shareListener = new HttpListener();
        using var session = new SwitcherService.PairTransferSession(shareListener, 0, "unused", new SwitcherService.PairTransferPackage(), "87654321");
        typeof(SwitcherService.PairTransferSession).GetProperty("TunnelUrl")!.SetValue(session, "https://synthetic.trycloudflare.com");
        typeof(MainWindow).GetField("activeTransfer", Private)!.SetValue(window, session);
        Click("TransferButton"); Click("Send"); Capture("send"); Click("Copy pair info");
        var clipboardTask = window.Clipboard!.TryGetTextAsync();
        Wait(() => clipboardTask.IsCompleted);
        Check(clipboardTask.Result?.Contains("87654321") == true, "Copy used the wrong pair code.");
        Click("Stop sharing");
        Check(Field<SwitcherService.PairTransferSession?>("activeTransfer") is null, "Stop sharing left the session active.");
        Escape();

        // Exercise capture cleanup without launching a game or accessing real sessions.
        service.SetCaptureEnabled(true);
        typeof(MainWindow).GetField("captureActive", Private)!.SetValue(window, true);
        typeof(MainWindow).GetMethod("ShowCapture", Private)!.Invoke(window, null);
        Field<TextBlock>("captureStep").Text = "RuneLite detected. Waiting for the captured session…";
        Capture("capture");
        Escape();
        Wait(() => !Find<Border>("DialogOverlay").IsVisible);
        Check(!service.IsCaptureEnabled(), "Cancelling left credential capture enabled.");
        typeof(MainWindow).GetMethod("ShowCapture", Private)!.Invoke(window, null);
        Check(Find<TextBlock>("DialogTitle").Text == "Waiting for your character", "Capture cannot be opened a second time.");
        Escape();

        Click("SettingsButton"); Capture("settings"); Escape();
        window.Width = 940; window.Height = 650;
        Capture("minimum");
        Check(Find<Button>("PlayButton").Bounds.Width > 180, "Minimum-width Play button is cramped.");
        var play = Find<Button>("PlayButton");
        Check(play.TranslatePoint(new Point(0, play.Bounds.Height), window)!.Value.Y < window.Height - 40, "Play is below the fold at minimum size.");
        Click("TransferButton"); Capture("receive-minimum"); Escape();
        window.Width = 1160; window.Height = 760;
        Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        Capture("light");
        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        window.ApplyHighContrast(true);
        Capture("high-contrast");
        Check(!Find<Border>("LandscapeImage").IsVisible, "Artwork remains behind text in high contrast.");
        window.ApplyHighContrast(false);
        Capture("final");
        window.Close();
        Check(!Directory.EnumerateFiles(isolated, "*.properties", SearchOption.AllDirectories)
            .Where(p => p.Contains("credentials")).Any(p => File.ReadAllText(p).Contains("JX_SESSION_ID")), "Vault credentials were stored in plaintext.");

        // Clean sample profiles for the README screenshot (docs/assets/launcher.png).
        isolated = Path.Combine(output, "synthetic-vault-" + Guid.NewGuid());
        Directory.CreateDirectory(isolated);
        service = new SwitcherService();
        foreach (var name in new[] { "vaultRoot", "liveCreds", "runeLiteExe", "runeLiteSettings", "jagexLauncherExe" })
            typeof(SwitcherService).GetField(name, Private)!.SetValue(service, Path.Combine(isolated, name));
        Seed("Main", "Oak & Ember");
        Seed("Ironman", "Iron Tadao");
        Seed("Pure", "Wildy scout");
        Seed("Skiller", "Quiet Anvil");
        var profilesFile = Path.Combine(isolated, "vaultRoot", "profiles.json");
        var vault = JsonNode.Parse(File.ReadAllText(profilesFile))!;
        vault["profiles"]!["Main"]!["lastPlayedAt"] = DateTimeOffset.Now.AddDays(-1).ToString("o");
        File.WriteAllText(profilesFile, vault.ToJsonString());
        window = new MainWindow(service);
        window.Show();
        Pump();
        list = Find<ListBox>("ProfileList");
        list.SelectedItem = list.Items.Cast<ProfileInfo>().Single(p => p.Name == "Main");
        Capture("readme");
        window.Close();
        Console.WriteLine($"PASS: service self-check, UI import/rename/removal, search, draft retention, transfer rejection/loopback receive/copy/stop, capture cancellation, keyboard, minimum-size and theme renders. {output}");

        void Seed(string name, string character)
        {
            File.WriteAllText(Path.Combine(isolated, "liveCreds"), Credentials(character));
            service.Import(name);
        }
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
        T Find<T>(string name) where T : Control => window.FindControl<T>(name) ?? throw new Exception($"Missing {name}");
        void Click(string name)
        {
            Pump();
            var button = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsVisible && (b.Name == name || b.Content as string == name))
                ?? throw new Exception($"Missing button: {name}");
            Check(button.IsEffectivelyEnabled, $"Button disabled: {name}");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
        }
        void SendKey(Key key) { window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null); window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null); Pump(); }
        void Escape() => SendKey(Key.Escape);
        void Capture(string name)
        {
            Pump();
            using var frame = window.CaptureRenderedFrame() ?? throw new Exception("No rendered frame.");
            frame.Save(Path.Combine(output, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static string Credentials(string character) => $"JX_DISPLAY_NAME={character}\nJX_SESSION_ID=synthetic-session\nJX_CHARACTER_ID=synthetic-character\nJX_ACCESS_TOKEN=synthetic-access\nJX_REFRESH_TOKEN=synthetic-refresh\n";
    private static void Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(10); }
        Check(condition(), "Timed out waiting for UI state.");
        Pump();
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
