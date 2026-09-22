using System.Reflection;
using JagexSwitcher.App;

// Run: dotnet run --project app/JagexSwitcher.VisualCheck -- .local/visual-check
// Synthetic data only. Service paths point to an isolated temporary directory.
internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        SwitcherService.SelfCheck();
        var output = Path.GetFullPath(args.FirstOrDefault() ?? ".local/visual-check");
        Directory.CreateDirectory(output);
        var service = new SwitcherService();
        var isolated = Path.Combine(Path.GetTempPath(), "switcher-visual-" + Guid.NewGuid());
        foreach (var name in new[] { "vaultRoot", "liveCreds", "runeLiteExe", "runeLiteSettings", "jagexLauncherExe" })
            typeof(SwitcherService).GetField(name, Private)!.SetValue(service, Path.Combine(isolated, name));
        using var form = new MainForm(service);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-4000, -4000);
        form.ShowInTaskbar = false;
        form.Show();
        Capture("empty");
        Click("Add your first account");
        var nameBox = Field<TextBox>("profileNameBox");
        nameBox.Text = "New ironman";
        Click("Transfer");
        Click("Receive");
        Field<TextBox>("pairUrlBox").Text = "https://example.trycloudflare.com";
        Capture("receive-empty");
        Click("Add account");
        Check(nameBox.Text == "New ironman" && !nameBox.IsDisposed, "Account draft was lost navigating tabs.");
        Capture("add");

        var list = Field<ListView>("profileList");
        foreach (var (name, character, status) in new[] {
            ("Main account", "Oak & Ember", "Ready"), ("Ironman", "Iron Tadao", "Ready"),
            ("Pure", "Wildy scout", "Missing"), ("A deliberately long profile name for truncation", "Long character name", "Unreadable") })
        {
            var item = new ListViewItem(new[] { name, "Today 14:32", status });
            item.Tag = new ProfileInfo(name, character, "2026-09-20T18:42:00Z", "2026-09-23T14:32:00Z", status);
            list.Items.Add(item);
        }
        Field<Label>("libraryCount").Text = "YOUR PROFILES   /   04";
        Field<Label>("statusLabel").Text = "4 profiles saved. Select a profile to play.";
        Field<Panel>("emptyPanel").Visible = false;
        list.Visible = true;
        list.Items[0].Selected = true;
        Click("Profile");
        Check(Find(form, "Play in RuneLite") is Button, "Selected profile has no Play action.");
        Capture("profiles");
        form.Size = form.MinimumSize;
        Capture("minimum");
        Check((GetWindowLong(list.Handle, -16) & 0x00100000) == 0, "Native horizontal scrollbar is visible.");
        Check(list.Columns.Cast<ColumnHeader>().Sum(column => column.Width) <= list.ClientSize.Width,
            "Profile columns overflow at minimum size.");
        Click("Transfer");
        Click("Receive");
        Check(Field<TextBox>("pairUrlBox").Text == "https://example.trycloudflare.com", "Transfer draft was lost.");
        Capture("receive-minimum");
        typeof(MainForm).GetMethod("FocusRenameBox", Private)!.Invoke(form, null);
        Check(Field<TextBox>("renameProfileBox").Focused, "F2 does not open and focus rename from another tab.");
        Field<CheckBox>("advancedSettingsBox").Checked = true;
        Capture("advanced-minimum");
        form.Size = new Size(1120, 780);
        Directory.CreateDirectory(isolated);
        service.SetCaptureEnabled(true);
        typeof(MainForm).GetField("addAccountActive", Private)!.SetValue(form, true);
        Field<Label>("addStepLabel").Text = "RuneLite detected. Waiting for captured credentials...";
        typeof(MainForm).GetMethod("RenderActions", Private)!.Invoke(form, null);
        Check(Field<FlowLayoutPanel>("navigation").Controls.Cast<Control>().All(c => !c.Enabled), "Capture navigation is not locked.");
        Check(Field<Label>("captureLabel").Visible, "Capture warning is missing.");
        Check(Field<Label>("captureLabel").Top >= 82, "Capture warning overlaps the header.");
        Capture("capture");
        Click("Cancel Add");
        Check(Field<FlowLayoutPanel>("navigation").Controls.Cast<Control>().All(c => c.Enabled), "Cancel did not restore navigation.");
        File.Delete(Path.Combine(isolated, "runeLiteSettings"));
        Directory.Delete(isolated);
        Console.WriteLine($"Service self-check and visual interaction checks passed. Renders: {output}");

        T Field<T>(string name) => (T)typeof(MainForm).GetField(name, Private)!.GetValue(form)!;
        void Click(string text)
        {
            var button = Find(form, text) as Button ?? throw new Exception($"Missing button: {text}");
            button.PerformClick();
            Application.DoEvents();
        }
        void Capture(string name)
        {
            Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(output, name + ".png"));
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    private static Control? Find(Control parent, string text) => parent.Controls.Cast<Control>()
        .Select(control => control.Text == text ? control : Find(control, text)).FirstOrDefault(control => control is not null);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
