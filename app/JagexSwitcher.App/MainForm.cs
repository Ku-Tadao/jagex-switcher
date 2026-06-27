namespace JagexSwitcher.App;

internal sealed class MainForm : Form
{
    // ponytail: palette is a handful of static colors; no theme engine, no skinning framework.
    // Ceiling: hard-coded dark theme only. Upgrade path: swap these for a runtime theme struct.
    private static readonly Color Bg = Color.FromArgb(27, 27, 41);
    private static readonly Color Surface = Color.FromArgb(38, 38, 56);
    private static readonly Color SurfaceAlt = Color.FromArgb(34, 34, 52);
    private static readonly Color Border = Color.FromArgb(51, 51, 74);
    private static readonly Color TextColor = Color.FromArgb(232, 232, 240);
    private static readonly Color Muted = Color.FromArgb(144, 144, 168);
    private static readonly Color Accent = Color.FromArgb(232, 177, 75);
    private static readonly Color AccentText = Color.FromArgb(27, 27, 41);
    private static readonly Color Danger = Color.FromArgb(224, 85, 107);
    private static readonly Color HeaderBg = Color.FromArgb(38, 38, 56);
    private static readonly Color HeaderFg = Color.FromArgb(160, 160, 184);
    private static readonly Color RowBg = Color.FromArgb(34, 34, 52);
    private static readonly Color RowBgAlt = Color.FromArgb(30, 30, 46);
    private static readonly Color RowSelected = Color.FromArgb(58, 51, 32);

    private static readonly Font BodyFont = new("Segoe UI", 9F);
    private static readonly Font SectionFont = new("Segoe UI", 8F, FontStyle.Bold);
    private static readonly Font FieldFont = new("Segoe UI", 9F);
    private static readonly Font HeaderFont = new("Segoe UI", 8.25F, FontStyle.Bold);

    // Right panel sizing: content narrower than the column so the AutoScroll vertical
    // scrollbar never overlaps fields and never triggers a horizontal scrollbar.
    // ponytail: assumes Windows scrollbar width ~17px; if a user themes it wider, bump RightColumnWidth.
    private const int RightColumnWidth = 340;
    private const int RightContentWidth = 280;

    private readonly SwitcherService switcher;
    private readonly ListView profileList = new();
    private readonly TextBox profileNameBox = new();
    private readonly Label captureLabel = new();
    private readonly Label statusLabel = new();
    private readonly CheckBox advancedSettingsBox = new();
    private readonly FlowLayoutPanel advancedPanel = new();
    private readonly TextBox pairUrlBox = new();
    private readonly TextBox pairCodeBox = new();
    private readonly System.Windows.Forms.Timer addAccountTimer = new();
    private Button? cancelAddButton;
    private Button? stopPairButton;
    private string? pendingProfileName;
    private SwitcherService.PairTransferSession? activePairTransfer;

    public MainForm(SwitcherService switcher)
    {
        this.switcher = switcher;

        Text = "Jagex Switcher";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 520);
        Size = new Size(960, 720);
        BackColor = Bg;
        Font = BodyFont;
        ForeColor = TextColor;
        DoubleBuffered = true;

        BuildLayout();
        addAccountTimer.Interval = 1000;
        addAccountTimer.Tick += (_, _) => WatchAddAccount();
        FormClosed += (_, _) => activePairTransfer?.Dispose();
        RefreshProfiles();
    }

    private void BuildLayout()
    {
        // --- List (left) ---
        profileList.View = View.Details;
        profileList.FullRowSelect = true;
        profileList.MultiSelect = false;
        profileList.HideSelection = false;
        profileList.Dock = DockStyle.Fill;
        profileList.BackColor = RowBg;
        profileList.ForeColor = TextColor;
        profileList.Font = BodyFont;
        profileList.OwnerDraw = true;
        profileList.DrawColumnHeader += OnDrawColumnHeader;
        profileList.DrawItem += OnDrawItem;
        profileList.DrawSubItem += OnDrawSubItem;
        profileList.Columns.Add("Profile", 150);
        profileList.Columns.Add("Display", 170);
        profileList.Columns.Add("Imported", 240);
        profileList.Columns.Add(""); // filler: absorbs leftover width so rows/headers fill the list
        profileList.ClientSizeChanged += (_, _) => FillListColumns();
        profileList.DoubleClick += (_, _) => PlaySelected();

        // --- Right action panel ---
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Bg,
            Padding = new Padding(16, 8, 0, 8)
        };

        // Profile section
        actions.Controls.Add(SectionHeader("Profile"));
        actions.Controls.Add(FieldLabel("Profile name"));
        profileNameBox.Width = RightContentWidth;
        StyleBox(profileNameBox);
        actions.Controls.Add(profileNameBox);
        actions.Controls.Add(NewButton("Add Account", (_, _) => StartAddAccount()));
        cancelAddButton = NewButton("Cancel Add", (_, _) => StopAddAccount("Account add cancelled."));
        cancelAddButton.Enabled = false;
        cancelAddButton.Visible = false;
        actions.Controls.Add(cancelAddButton);
        actions.Controls.Add(NewButton("Add Current", (_, _) => ImportCurrent()));

        // Library section
        actions.Controls.Add(SectionHeader("Library"));
        actions.Controls.Add(NewButton("Play Selected", (_, _) => PlaySelected(), BtnStyle.Primary));
        actions.Controls.Add(ButtonRow(
            NewButton("Remove", (_, _) => RemoveSelected(), BtnStyle.Danger),
            NewButton("Refresh", (_, _) => RefreshProfiles())));

        // Pair transfer section
        actions.Controls.Add(SectionHeader("Pair Transfer"));
        actions.Controls.Add(FieldLabel("Pair URL"));
        pairUrlBox.Width = RightContentWidth;
        StyleBox(pairUrlBox);
        actions.Controls.Add(pairUrlBox);
        actions.Controls.Add(FieldLabel("Pair code"));
        pairCodeBox.Width = RightContentWidth;
        StyleBox(pairCodeBox);
        actions.Controls.Add(pairCodeBox);
        actions.Controls.Add(ButtonRow(
            NewButton("Send", async (_, _) => await StartPairTransfer()),
            NewButton("Receive", async (_, _) => await ReceivePair())));
        actions.Controls.Add(NewButton("Copy Pair Info", (_, _) => CopyPairInfo()));
        stopPairButton = NewButton("Stop Share", (_, _) => StopPairTransfer("Pair transfer stopped."), BtnStyle.Danger);
        stopPairButton.Enabled = false;
        stopPairButton.Visible = false;
        actions.Controls.Add(stopPairButton);

        // Advanced section
        advancedSettingsBox.Text = "Advanced settings";
        advancedSettingsBox.Width = RightContentWidth;
        advancedSettingsBox.Height = 28;
        advancedSettingsBox.Font = BodyFont;
        advancedSettingsBox.ForeColor = TextColor;
        advancedSettingsBox.BackColor = Bg;
        advancedSettingsBox.Margin = new Padding(0, 18, 0, 8);
        advancedSettingsBox.CheckedChanged += (_, _) => UpdateAdvancedUi();
        actions.Controls.Add(advancedSettingsBox);

        advancedPanel.FlowDirection = FlowDirection.TopDown;
        advancedPanel.WrapContents = false;
        advancedPanel.AutoSize = true;
        advancedPanel.Width = RightContentWidth;
        advancedPanel.Visible = false;
        advancedPanel.BackColor = Bg;
        advancedPanel.Controls.Add(NewButton("Prepare Login", (_, _) => RunAction(() => statusLabel.Text = switcher.PrepareLogin())));
        advancedPanel.Controls.Add(NewButton("Capture Login", (_, _) => RunAction(() =>
        {
            switcher.StartCaptureLogin();
            statusLabel.Text = "Capture mode on; launcher opened.";
        })));
        advancedPanel.Controls.Add(NewButton("Capture On", (_, _) => RunAction(() =>
        {
            switcher.SetCaptureEnabled(true);
            statusLabel.Text = "Capture mode on.";
        })));
        advancedPanel.Controls.Add(NewButton("Capture Off", (_, _) => RunAction(() =>
        {
            switcher.SetCaptureEnabled(false);
            statusLabel.Text = "Capture mode off.";
        })));
        actions.Controls.Add(advancedPanel);

        // --- Main body (list + actions) ---
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Bg,
            Padding = new Padding(16)
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, RightColumnWidth));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(profileList, 0, 0);
        body.Controls.Add(actions, 1, 0);

        // --- Status bar ---
        var statusBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 30,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Surface,
            Padding = new Padding(16, 0, 16, 0)
        };
        captureLabel.AutoSize = true;
        captureLabel.Font = BodyFont;
        captureLabel.ForeColor = Muted;
        captureLabel.Margin = new Padding(0, 7, 0, 7);
        statusLabel.AutoSize = true;
        statusLabel.Font = BodyFont;
        statusLabel.ForeColor = TextColor;
        statusLabel.Margin = new Padding(0, 7, 0, 7);
        statusBar.Controls.Add(captureLabel);
        statusBar.Controls.Add(statusLabel);

        // --- Header bar ---
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 56,
            BackColor = Surface
        };
        var subtitle = new Label
        {
            Text = "OSRS / RuneLite profile manager",
            Font = new Font("Segoe UI", 8.25F),
            ForeColor = Muted,
            Dock = DockStyle.Top,
            Height = 18,
            Padding = new Padding(16, 0, 0, 4),
            BackColor = Surface
        };
        var title = new Label
        {
            Text = "Jagex Switcher",
            Font = new Font("Segoe UI Semibold", 15F),
            ForeColor = TextColor,
            Dock = DockStyle.Top,
            Height = 30,
            Padding = new Padding(16, 6, 0, 0),
            BackColor = Surface
        };
        header.Controls.Add(subtitle);
        header.Controls.Add(title);

        var separator = new Panel
        {
            Dock = DockStyle.Top,
            Height = 1,
            BackColor = Border
        };

        var accentStripe = new Panel
        {
            Dock = DockStyle.Top,
            Height = 3,
            BackColor = Accent
        };

        // Dock order: add Fill first, then Bottom, then Top-docked in reverse visual order.
        Controls.Add(body);
        Controls.Add(statusBar);
        Controls.Add(separator);
        Controls.Add(header);
        Controls.Add(accentStripe);

        FillListColumns();
    }

    private static void StyleBox(TextBox box)
    {
        box.BackColor = SurfaceAlt;
        box.ForeColor = TextColor;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.Font = BodyFont;
        box.Margin = new Padding(0, 0, 0, 8);
    }

    private enum BtnStyle { Default, Primary, Danger }

    private static Button NewButton(string text, EventHandler click, BtnStyle style = BtnStyle.Default, int width = RightContentWidth)
    {
        var button = new Button
        {
            Text = text,
            Width = width,
            Height = 32,
            FlatStyle = FlatStyle.Flat,
            Font = BodyFont,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Surface,
            ForeColor = TextColor,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 0, 6)
        };
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = SurfaceAlt;
        button.FlatAppearance.MouseDownBackColor = Border;

        if (style == BtnStyle.Primary)
        {
            button.BackColor = Accent;
            button.ForeColor = AccentText;
            button.Font = new Font("Segoe UI Semibold", 9F);
            button.FlatAppearance.BorderColor = Accent;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(245, 200, 110);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(212, 165, 80);
        }
        else if (style == BtnStyle.Danger)
        {
            button.ForeColor = Danger;
            button.FlatAppearance.BorderColor = Danger;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 32, 40);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(80, 40, 50);
        }

        button.Click += click;
        return button;
    }

    private static FlowLayoutPanel ButtonRow(Button left, Button right)
    {
        var each = (RightContentWidth - 6) / 2;
        left.Width = each;
        right.Width = each;
        left.Margin = new Padding(0, 0, 6, 0);
        right.Margin = new Padding(0, 0, 0, 0);
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Width = RightContentWidth,
            Height = 32,
            Margin = new Padding(0, 0, 0, 6),
            BackColor = Bg
        };
        row.Controls.Add(left);
        row.Controls.Add(right);
        return row;
    }

    private static Label SectionHeader(string text)
    {
        return new Label
        {
            Text = text.ToUpperInvariant(),
            Font = SectionFont,
            ForeColor = Muted,
            Width = RightContentWidth,
            Height = 16,
            Margin = new Padding(0, 12, 0, 4),
            BackColor = Bg
        };
    }

    private static Label FieldLabel(string text)
    {
        return new Label
        {
            Text = text,
            Font = FieldFont,
            ForeColor = Muted,
            Width = RightContentWidth,
            Height = 18,
            Margin = new Padding(0, 2, 0, 2),
            BackColor = Bg
        };
    }

    private void FillListColumns()
    {
        var cols = profileList.Columns;
        if (cols.Count == 0)
        {
            return;
        }

        var used = 0;
        for (var i = 0; i < cols.Count - 1; i++)
        {
            used += cols[i].Width;
        }

        var remaining = profileList.ClientSize.Width - used;
        cols[cols.Count - 1].Width = remaining > 0 ? remaining : 0;
    }

    private void OnDrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using var brush = new SolidBrush(HeaderBg);
        e.Graphics.FillRectangle(brush, e.Bounds);
        TextRenderer.DrawText(
            e.Graphics,
            e.Header.Text,
            HeaderFont,
            e.Bounds,
            HeaderFg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.LeftAndRightPadding);
    }

    private void OnDrawItem(object? sender, DrawListViewItemEventArgs e)
    {
        var bg = e.Item.Selected
            ? RowSelected
            : ((e.ItemIndex & 1) == 0 ? RowBg : RowBgAlt);
        using var brush = new SolidBrush(bg);
        e.Graphics.FillRectangle(brush, e.Bounds);
    }

    private void OnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item.SubItems.Count <= e.ColumnIndex)
        {
            return;
        }

        var fg = e.Item.Selected ? Color.White : TextColor;
        TextRenderer.DrawText(
            e.Graphics,
            e.SubItem.Text,
            BodyFont,
            e.Bounds,
            fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.LeftAndRightPadding);
    }

    private void RefreshProfiles()
    {
        RunAction(LoadProfiles);
    }

    private void LoadProfiles()
    {
        profileList.BeginUpdate();
        try
        {
            profileList.Items.Clear();
            var profiles = switcher.GetProfiles();
            foreach (var profile in profiles)
            {
                var item = new ListViewItem(profile.Name);
                item.SubItems.Add(profile.DisplayName);
                item.SubItems.Add(profile.ImportedAt);
                item.Tag = profile.Name;
                profileList.Items.Add(item);
            }

            UpdateCaptureStatus();
            statusLabel.Text = profiles.Count == 0 ? "No profiles saved." : $"{profiles.Count} profile(s).";
        }
        finally
        {
            profileList.EndUpdate();
        }
    }

    private void ImportCurrent()
    {
        var profileName = profileNameBox.Text.Trim();
        RunAction(() =>
        {
            switcher.Import(profileName);
            profileNameBox.Clear();
            LoadProfiles();
            statusLabel.Text = $"Imported profile '{profileName}'.";
        });
    }

    private void StartAddAccount()
    {
        var profileName = profileNameBox.Text.Trim();
        RunAction(() =>
        {
            if (string.IsNullOrWhiteSpace(profileName))
            {
                throw new InvalidOperationException("Enter a profile name first.");
            }

            if (switcher.IsRuneLiteRunning())
            {
                throw new InvalidOperationException("Close RuneLite before adding an account so the app can track the new Jagex-launched client.");
            }

            if (switcher.IsOfficialOldSchoolClientRunning())
            {
                throw new InvalidOperationException("Close the official Old School client first. Add Account needs RuneLite.");
            }

            if (!switcher.IsCaptureEnabled())
            {
                switcher.SetCaptureEnabled(true);
            }

            pendingProfileName = profileName;
            cancelAddButton!.Enabled = true;
            cancelAddButton.Visible = true;
            switcher.BeginAddAccount();
            addAccountTimer.Start();
            statusLabel.Text = "Jagex Launcher opened. Log in, pick RuneLite, then start the character.";
        });
    }

    private void WatchAddAccount()
    {
        if (pendingProfileName is null)
        {
            return;
        }

        try
        {
            if (switcher.IsOfficialOldSchoolClientRunning())
            {
                StopAddAccount(null);
                ShowError("Old School's official client launched. Choose RuneLite in the Jagex Launcher, then try Add Account again.");
                return;
            }

            var jagexRunning = switcher.IsJagexLauncherRunning();
            var runeLiteRunning = switcher.IsRuneLiteRunning();

            if (runeLiteRunning && !jagexRunning)
            {
                StopAddAccount(null);
                ShowError("RuneLite is running, but Jagex Launcher is not. Start RuneLite through the Jagex Launcher for capture.");
                return;
            }

            if (!jagexRunning)
            {
                statusLabel.Text = "Waiting for Jagex Launcher...";
                return;
            }

            if (!runeLiteRunning)
            {
                statusLabel.Text = "Jagex Launcher detected. Start RuneLite for the character you want to add.";
                return;
            }

            if (!switcher.HasCapturedCredentials())
            {
                statusLabel.Text = "RuneLite detected. Waiting for captured credentials...";
                return;
            }

            var profileName = pendingProfileName;
            switcher.Import(profileName);
            profileNameBox.Clear();
            LoadProfiles();
            StopAddAccount($"Added profile '{profileName}'.");
        }
        catch (Exception ex)
        {
            StopAddAccount(null);
            ShowError(ex.Message);
        }
    }

    private void StopAddAccount(string? message)
    {
        addAccountTimer.Stop();
        pendingProfileName = null;
        if (cancelAddButton is not null)
        {
            cancelAddButton.Enabled = false;
            cancelAddButton.Visible = false;
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            statusLabel.Text = message;
        }
    }

    private void PlaySelected()
    {
        var profileName = SelectedProfileName();
        if (profileName is null)
        {
            ShowError("Select a profile first.");
            return;
        }

        RunAction(() =>
        {
            if (switcher.IsCaptureEnabled() && !advancedSettingsBox.Checked)
            {
                switcher.SetCaptureEnabled(false);
            }

            switcher.Play(profileName);
            statusLabel.Text = $"Playing profile '{profileName}'.";
        });
    }

    private void RemoveSelected()
    {
        var profileName = SelectedProfileName();
        if (profileName is null)
        {
            ShowError("Select a profile first.");
            return;
        }

        var choice = MessageBox.Show(
            $"Remove profile '{profileName}'?",
            "Jagex Switcher",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (choice != DialogResult.Yes)
        {
            return;
        }

        RunAction(() =>
        {
            switcher.Remove(profileName);
            LoadProfiles();
            statusLabel.Text = $"Removed profile '{profileName}'.";
        });
    }

    private async Task StartPairTransfer()
    {
        var profileName = SelectedProfileName();
        if (profileName is null)
        {
            ShowError("Select a profile first.");
            return;
        }

        await RunActionAsync(async () =>
        {
            activePairTransfer?.Dispose();
            statusLabel.Text = "Preparing Cloudflare tunnel...";

            var session = await switcher.StartPairTransferAsync(profileName);
            activePairTransfer = session;
            session.Completed += () => BeginInvoke((Action)(() =>
            {
                if (activePairTransfer == session)
                {
                    StopPairTransfer("Pair transfer completed.");
                }
            }));

            pairUrlBox.Text = session.TunnelUrl;
            pairCodeBox.Text = session.Code;
            stopPairButton!.Enabled = true;
            stopPairButton.Visible = true;
            CopyPairInfo();
            statusLabel.Text = "Pair info copied. Keep this app open until the laptop imports it.";
        });
    }

    private async Task ReceivePair()
    {
        var pairUrl = pairUrlBox.Text.Trim();
        var pairCode = pairCodeBox.Text.Trim();
        var profileName = profileNameBox.Text.Trim();

        await RunActionAsync(async () =>
        {
            var importedName = await switcher.ReceivePairAsync(pairUrl, pairCode, profileName);
            profileNameBox.Clear();
            LoadProfiles();
            statusLabel.Text = $"Imported paired profile '{importedName}'.";
        });
    }

    private void CopyPairInfo()
    {
        if (string.IsNullOrWhiteSpace(pairUrlBox.Text) || string.IsNullOrWhiteSpace(pairCodeBox.Text))
        {
            ShowError("No pair info to copy.");
            return;
        }

        Clipboard.SetText($"Pair URL: {pairUrlBox.Text.Trim()}{Environment.NewLine}Pair code: {pairCodeBox.Text.Trim()}");
    }

    private void StopPairTransfer(string message)
    {
        activePairTransfer?.Dispose();
        activePairTransfer = null;
        if (stopPairButton is not null)
        {
            stopPairButton.Enabled = false;
            stopPairButton.Visible = false;
        }

        statusLabel.Text = message;
    }

    private string? SelectedProfileName()
    {
        return profileList.SelectedItems.Count == 0
            ? null
            : profileList.SelectedItems[0].Tag as string;
    }

    private void RunAction(Action action)
    {
        try
        {
            action();
            UpdateCaptureStatus();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        try
        {
            await action();
            UpdateCaptureStatus();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void UpdateAdvancedUi()
    {
        advancedPanel.Visible = advancedSettingsBox.Checked;
        UpdateCaptureStatus();
    }

    private void UpdateCaptureStatus()
    {
        var cap = advancedSettingsBox.Checked
            ? switcher.IsCaptureEnabled() ? "Capture: on" : "Capture: off"
            : "";
        captureLabel.Text = cap;
        // No right margin when empty, so the status text sits flush-left.
        captureLabel.Margin = new Padding(0, 7, string.IsNullOrEmpty(cap) ? 0 : 16, 7);
    }

    private static void ShowError(string message)
    {
        MessageBox.Show(message, "Jagex Switcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
