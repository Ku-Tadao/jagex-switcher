namespace JagexSwitcher.App;

internal sealed class MainForm : Form
{
    // ponytail: static palette, no theme engine. Add a full theme model only if users ask for custom themes.
    // Ceiling: reads Windows theme at startup only. Upgrade path: listen for WM_SETTINGCHANGE.
    private static readonly bool HighContrast = SystemInformation.HighContrast;
    private static readonly bool LightTheme = !HighContrast && IsWindowsLightTheme();
    private static readonly Color Bg = HighContrast ? SystemColors.Window : LightTheme ? Color.FromArgb(240, 242, 240) : Color.FromArgb(18, 23, 23);
    private static readonly Color Surface = HighContrast ? SystemColors.Control : LightTheme ? Color.White : Color.FromArgb(27, 34, 33);
    private static readonly Color SurfaceAlt = HighContrast ? SystemColors.ControlLight : LightTheme ? Color.FromArgb(230, 234, 230) : Color.FromArgb(35, 44, 41);
    private static readonly Color Border = HighContrast ? SystemColors.WindowText : LightTheme ? Color.FromArgb(182, 193, 184) : Color.FromArgb(60, 73, 66);
    private static readonly Color TextColor = HighContrast ? SystemColors.WindowText : LightTheme ? Color.FromArgb(27, 38, 32) : Color.FromArgb(237, 239, 230);
    private static readonly Color Muted = HighContrast ? SystemColors.GrayText : LightTheme ? Color.FromArgb(83, 100, 89) : Color.FromArgb(161, 177, 164);
    private static readonly Color Accent = HighContrast ? SystemColors.Highlight : LightTheme ? Color.FromArgb(129, 89, 24) : Color.FromArgb(218, 184, 113);
    private static readonly Color AccentText = HighContrast ? SystemColors.HighlightText : LightTheme ? Color.FromArgb(255, 252, 244) : Color.FromArgb(18, 23, 23);
    private static readonly Color Danger = HighContrast ? SystemColors.HotTrack : LightTheme ? Color.FromArgb(166, 49, 52) : Color.FromArgb(241, 151, 139);
    private static readonly Color RowBg = Surface;
    private static readonly Color RowSelected = HighContrast ? SystemColors.Highlight : LightTheme ? Color.FromArgb(229, 233, 215) : Color.FromArgb(48, 58, 45);
    private static readonly Color WarningBg = HighContrast ? SystemColors.Control : LightTheme ? Color.FromArgb(255, 238, 197) : Color.FromArgb(61, 48, 30);

    private static readonly Font BodyFont = new("Segoe UI", 10F);
    private static readonly Font BodyStrongFont = new("Segoe UI Semibold", 10F);
    private static readonly Font TitleFont = new("Segoe UI Semibold", 23F);
    private static readonly Font ProfileTitleFont = new("Segoe UI Semibold", 17F);
    private static readonly Font SectionFont = new("Segoe UI", 8F, FontStyle.Bold);
    private static readonly Font HeaderFont = new("Segoe UI", 8.25F, FontStyle.Bold);

    private const int RightColumnWidth = 360;
    private const int RightContentWidth = 296;
    private const int StatusColumnIndex = 2;

    private readonly SwitcherService switcher;
    private readonly ListView profileList = new();
    private readonly Panel profileHost = new();
    private readonly Panel emptyPanel = new();
    private readonly Label libraryCount = new();
    private readonly FlowLayoutPanel navigation = new();
    private readonly ImageList rowHeight = new() { ImageSize = new Size(1, 72) };
    private readonly FlowLayoutPanel actions = new();
    private readonly Label captureLabel = new();
    private readonly Label statusLabel = new();
    private readonly Label addStepLabel = new();
    private readonly CheckBox advancedSettingsBox = new();
    private readonly FlowLayoutPanel advancedPanel = new();
    private readonly TextBox profileNameBox = new();
    private readonly TextBox renameProfileBox = new();
    private readonly TextBox pairUrlBox = new();
    private readonly TextBox pairCodeBox = new();
    private readonly TextBox receiveNameBox = new();
    private readonly System.Windows.Forms.Timer addAccountTimer = new();
    private readonly HashSet<Control> persistentControls;
    private PairMode pairMode = PairMode.Send;
    private ActionPage actionPage = ActionPage.Profile;
    private bool addAccountActive;
    private bool busy;
    private int captureStableTicks;
    private string? pendingProfileName;
    private SwitcherService.PairTransferSession? activePairTransfer;
    private readonly HashSet<int> hoverRepaintedRows = new();

    private enum BtnStyle { Default, Primary, Danger }
    private enum PairMode { Send, Receive }
    private enum ActionPage { Profile, Add, Transfer }

    public MainForm(SwitcherService switcher)
    {
        this.switcher = switcher;

        var version = typeof(MainForm).Assembly.GetName().Version;
        Text = version is null || (version.Major == 0 && version.Minor == 0 && version.Build == 0)
            ? "Jagex Switcher"
            : $"Jagex Switcher v{version.ToString(3)}";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        MinimumSize = new Size(960, 640);
        Size = new Size(1120, 780);
        BackColor = Bg;
        Font = BodyFont;
        ForeColor = TextColor;
        DoubleBuffered = true;
        KeyPreview = true;
        KeyDown += OnFormKeyDown;

        profileNameBox.PlaceholderText = "Blank uses character name";
        receiveNameBox.PlaceholderText = "Blank uses sent name";
        pairUrlBox.PlaceholderText = "https://....trycloudflare.com";
        pairCodeBox.PlaceholderText = "8-digit code";

        persistentControls = new HashSet<Control>
        {
            profileNameBox, renameProfileBox, pairUrlBox, pairCodeBox,
            receiveNameBox, addStepLabel, advancedSettingsBox, advancedPanel
        };

        BuildLayout();
        addAccountTimer.Interval = 1000;
        addAccountTimer.Tick += (_, _) => WatchAddAccount();
        FormClosed += (_, _) =>
        {
            addAccountTimer.Dispose();
            rowHeight.Dispose();
            activePairTransfer?.Dispose();
        };
        RefreshProfiles();
    }

    private void BuildLayout()
    {
        ConfigureProfileList();
        BuildEmptyPanel();
        BuildAdvancedPanel();

        profileHost.Dock = DockStyle.Fill;
        profileHost.BackColor = Bg;
        profileHost.Controls.Add(profileList);
        profileHost.Controls.Add(emptyPanel);

        var library = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Margin = new Padding(0, 0, 24, 0) };
        library.Controls.Add(profileHost);
        var libraryHeader = new Panel { Dock = DockStyle.Top, Height = 68, BackColor = Surface };
        libraryCount.Text = "YOUR PROFILES";
        libraryCount.Font = BodyStrongFont;
        libraryCount.ForeColor = TextColor;
        libraryCount.Dock = DockStyle.Fill;
        libraryCount.Padding = new Padding(20, 24, 0, 0);
        var refresh = NewButton("Refresh  F5", (_, _) => RefreshProfiles());
        refresh.Dock = DockStyle.Right;
        refresh.Width = 112;
        refresh.FlatAppearance.BorderSize = 0;
        libraryHeader.Controls.Add(libraryCount);
        libraryHeader.Controls.Add(refresh);
        library.Controls.Add(libraryHeader);
        var hint = new Label { Text = "Enter to play   /   F2 to rename   /   Right-click for more", Dock = DockStyle.Bottom,
            Height = 40, Padding = new Padding(20, 10, 0, 0), ForeColor = Muted, Font = HeaderFont };
        library.Controls.Add(hint);

        actions.Dock = DockStyle.Fill;
        actions.FlowDirection = FlowDirection.TopDown;
        actions.WrapContents = false;
        actions.AutoScroll = true;
        actions.BackColor = Bg;
        actions.Padding = new Padding(12, 8, 0, 16);

        var sidebar = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        navigation.Dock = DockStyle.Top;
        navigation.Height = 48;
        navigation.WrapContents = false;
        navigation.Padding = new Padding(12, 0, 0, 0);
        sidebar.Controls.Add(actions);
        sidebar.Controls.Add(navigation);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Bg,
            Padding = new Padding(24, 20, 12, 20)
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, RightColumnWidth));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(library, 0, 0);
        body.Controls.Add(sidebar, 1, 0);

        Controls.Add(body);
        Controls.Add(BuildStatusBar());
        Controls.Add(BuildHeader());
    }

    private void ConfigureProfileList()
    {
        profileList.View = View.Details;
        profileList.FullRowSelect = true;
        profileList.MultiSelect = false;
        profileList.HideSelection = false;
        profileList.Dock = DockStyle.Fill;
        profileList.BackColor = RowBg;
        profileList.ForeColor = TextColor;
        profileList.Font = BodyFont;
        profileList.OwnerDraw = true;
        profileList.BorderStyle = BorderStyle.None;
        profileList.SmallImageList = rowHeight;
        profileList.HandleCreated += (_, _) => rowHeight.ImageSize = new Size(1, (int)(72 * profileList.DeviceDpi / 96f));
        profileList.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        profileList.AccessibleName = "Saved profiles";
        profileList.DrawColumnHeader += OnDrawColumnHeader;
        profileList.DrawSubItem += OnDrawSubItem;
        profileList.SelectedIndexChanged += (_, _) => RenderActions();
        profileList.ClientSizeChanged += (_, _) =>
        {
            // Size columns after Win32 finishes resizing its scrollable client area.
            if (profileList.IsHandleCreated)
                profileList.BeginInvoke((Action)FillListColumns);
        };
        // ponytail: MS OwnerDraw workaround — Win32 can fire DrawItem without DrawSubItem on hover.
        profileList.MouseMove += OnProfileListMouseMove;
        profileList.Invalidated += OnProfileListInvalidated;
        profileList.DoubleClick += (_, _) => PlaySelected();
        profileList.ContextMenuStrip = BuildProfileContextMenu();

        typeof(Control)
            .GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(profileList, true);

        profileList.Columns.Add("Profile / character", 200);
        profileList.Columns.Add("Last played", 120);
        profileList.Columns.Add("Status", 100);
    }

    private ContextMenuStrip BuildProfileContextMenu()
    {
        var menu = new ContextMenuStrip
        {
            BackColor = Surface,
            ForeColor = TextColor,
            ShowImageMargin = false
        };
        menu.Items.Add("Play", null, (_, _) => PlaySelected());
        menu.Items.Add("Re-import", null, (_, _) =>
        {
            var name = SelectedProfileName();
            if (name is not null)
            {
                ImportCurrent(name);
            }
        });
        menu.Items.Add("Rename  (F2)", null, (_, _) => FocusRenameBox());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open vault folder", null, (_, _) => OpenVaultFolder());
        var remove = new ToolStripMenuItem("Remove  (Del)", null, (_, _) => RemoveSelected())
        {
            ForeColor = Danger
        };
        menu.Items.Add(remove);
        menu.Opening += (_, e) => e.Cancel = addAccountActive || busy || SelectedProfile() is null;
        return menu;
    }

    private void OnFormKeyDown(object? sender, KeyEventArgs e)
    {
        if (busy)
        {
            return;
        }

        if (addAccountActive)
        {
            if (e.KeyCode == Keys.Escape)
            {
                StopAddAccount("Account add cancelled.");
                e.Handled = true;
            }

            return;
        }

        switch (e.KeyCode)
        {
            case Keys.F5:
                RefreshProfiles();
                e.Handled = true;
                break;
            case Keys.F2 when SelectedProfile() is not null:
                FocusRenameBox();
                e.Handled = true;
                break;
            case Keys.Delete when profileList.Focused && SelectedProfile() is not null:
                RemoveSelected();
                e.Handled = true;
                break;
            case Keys.Enter when profileList.Focused && SelectedProfile() is not null:
                PlaySelected();
                e.Handled = true;
                e.SuppressKeyPress = true;
                break;
        }
    }

    private void FocusRenameBox()
    {
        actionPage = ActionPage.Profile;
        RenderActions();
        if (renameProfileBox.Parent is null)
        {
            return;
        }

        renameProfileBox.Focus();
        renameProfileBox.SelectAll();
    }

    private void BuildEmptyPanel()
    {
        emptyPanel.Dock = DockStyle.Fill;
        emptyPanel.BackColor = Surface;
        emptyPanel.Padding = new Padding(28, 38, 28, 20);
        var content = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true };
        var title = new Label { Text = "Your next adventure\nstarts here.", Font = TitleFont,
            ForeColor = TextColor, AutoSize = true, Margin = new Padding(0, 0, 0, 20) };
        var copy = new Label { Text = "Save your characters once. Choose a profile and get straight back to Gielinor.",
            Font = BodyFont, ForeColor = Muted, AutoSize = true, MaximumSize = new Size(350, 0),
            Margin = new Padding(0, 0, 0, 28) };
        var steps = new Label { Text = "01   Open Jagex Launcher\n\n02   Start your character in RuneLite\n\n03   Save the captured profile", Font = BodyFont,
            ForeColor = TextColor, AutoSize = true, Margin = new Padding(0, 0, 0, 28) };
        var add = NewButton("Add your first account", (_, _) => ShowPage(ActionPage.Add), BtnStyle.Primary);
        content.Controls.AddRange(new Control[] { title, copy, steps, add });
        emptyPanel.Controls.Add(content);
    }

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 96, BackColor = Bg,
            Padding = new Padding(24, 12, 24, 0) };
        captureLabel.Dock = DockStyle.Bottom;
        captureLabel.Height = 26;
        captureLabel.Font = BodyFont;
        captureLabel.ForeColor = Accent;
        captureLabel.BackColor = WarningBg;
        captureLabel.Padding = new Padding(8, 3, 0, 0);
        captureLabel.Visible = false;
        var subtitle = new Label { Text = "OLD SCHOOL RUNESCAPE  /  RUNELITE", Font = HeaderFont,
            ForeColor = Muted, Dock = DockStyle.Top, Height = 24, Padding = new Padding(2, 6, 0, 0) };
        var title = new Label { Text = "Jagex Switcher", Font = TitleFont, ForeColor = TextColor,
            Dock = DockStyle.Top, Height = 46 };
        header.Controls.Add(captureLabel);
        header.Controls.Add(subtitle);
        header.Controls.Add(title);
        return header;
    }

    private Control BuildStatusBar()
    {
        var statusBar = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            BackColor = Surface
        };

        statusLabel.Dock = DockStyle.Fill;
        statusLabel.Font = BodyFont;
        statusLabel.ForeColor = TextColor;
        statusLabel.Padding = new Padding(24, 10, 24, 0);
        statusLabel.BackColor = Surface;
        statusBar.Controls.Add(statusLabel);
        return statusBar;
    }

    private void BuildAdvancedPanel()
    {
        advancedPanel.FlowDirection = FlowDirection.TopDown;
        advancedPanel.WrapContents = false;
        advancedPanel.AutoSize = true;
        advancedPanel.Width = RightContentWidth;
        advancedPanel.Visible = false;
        advancedPanel.BackColor = Bg;
        advancedPanel.Controls.Add(NewButton("Prepare Login", (_, _) => RunAction(() => SetStatus(switcher.PrepareLogin()))));
        advancedPanel.Controls.Add(NewButton("Capture Login", (_, _) => RunAction(() =>
        {
            switcher.StartCaptureLogin();
            SetStatus("Capture mode on; launcher opened.");
        })));
        advancedPanel.Controls.Add(ButtonRow(
            NewButton("Capture On", (_, _) => RunAction(() =>
            {
                switcher.SetCaptureEnabled(true);
                SetStatus("Capture mode on.");
            })),
            NewButton("Capture Off", (_, _) => RunAction(() =>
            {
                switcher.SetCaptureEnabled(false);
                SetStatus("Capture mode off.");
            }))));
        advancedPanel.Controls.Add(NewButton("Forget All Profiles", (_, _) => ForgetAllProfiles(), BtnStyle.Danger));
    }

    private void RenderActions()
    {
        actions.SuspendLayout();
        ClearActions();

        if (addAccountActive)
        {
            RenderAddFlow();
        }
        else
        {
            if (actionPage == ActionPage.Transfer)
                RenderPairTransfer();
            else if (actionPage == ActionPage.Add)
                RenderAddActions();
            else
            {
                RenderProfileActions();
                RenderAdvanced();
            }
        }

        actions.ResumeLayout();
        RenderNavigation();
        UpdateCaptureStatus();
    }

    private void ShowPage(ActionPage page)
    {
        actionPage = page;
        actions.AutoScrollPosition = Point.Empty;
        RenderActions();
    }

    private void RenderNavigation()
    {
        if (navigation.Controls.Count == 0)
        {
            foreach (var (page, title) in new[] { (ActionPage.Profile, "Profile"), (ActionPage.Add, "Add account"), (ActionPage.Transfer, "Transfer") })
            {
                var button = NewButton(title, (_, _) => ShowPage(page));
                button.Width = (RightContentWidth - 8) / 3;
                button.Margin = new Padding(0, 0, 4, 0);
                button.Tag = page;
                navigation.Controls.Add(button);
            }
        }
        foreach (Button button in navigation.Controls)
        {
            var selected = actionPage == (ActionPage)button.Tag!;
            button.Enabled = !addAccountActive;
            button.BackColor = selected ? RowSelected : Bg;
            button.ForeColor = selected ? (HighContrast ? SystemColors.HighlightText : Accent) : Muted;
            button.FlatAppearance.BorderColor = selected ? Accent : Border;
            button.AccessibleDescription = selected ? "Current view" : "Open view";
        }
    }

    private void RenderAddActions()
    {
        actions.Controls.Add(SectionHeader("Add account"));
        actions.Controls.Add(InfoLabel("Open Jagex Launcher and start a character in RuneLite. We'll save the captured session here."));
        AddProfileNameField("Profile name (optional)");
        actions.Controls.Add(NewButton("Add Account", (_, _) => StartAddAccount(), BtnStyle.Primary));
        actions.Controls.Add(SectionHeader("Already signed in?"));
        actions.Controls.Add(InfoLabel("Use Add Current if RuneLite has already written valid credentials for this character."));
        actions.Controls.Add(NewButton("Add Current", (_, _) => ImportCurrent()));
    }

    private void ClearActions()
    {
        for (var i = actions.Controls.Count - 1; i >= 0; i--)
        {
            var control = actions.Controls[i];
            actions.Controls.RemoveAt(i);
            if (!persistentControls.Contains(control))
            {
                control.Dispose();
            }
        }
    }

    private void RenderAddFlow()
    {
        actions.Controls.Add(SectionHeader("Add account"));
        actions.Controls.Add(InfoLabel("Jagex Launcher is open. Pick RuneLite, start the character, and keep this app open while credentials are captured."));
        addStepLabel.Width = RightContentWidth;
        addStepLabel.Height = 48;
        addStepLabel.Font = BodyStrongFont;
        addStepLabel.ForeColor = TextColor;
        addStepLabel.BackColor = Bg;
        addStepLabel.Margin = new Padding(0, 6, 0, 8);
        actions.Controls.Add(addStepLabel);
        actions.Controls.Add(NewButton("Cancel Add", (_, _) => StopAddAccount("Account add cancelled."), BtnStyle.Danger));
    }

    private void RenderProfileActions()
    {
        var selected = SelectedProfile();
        actions.Controls.Add(SectionHeader(selected is null ? "Start" : "Selected profile"));

        if (selected is null)
        {
            actions.Controls.Add(InfoLabel("Select a saved profile to play, or add an account to begin."));
            actions.Controls.Add(SectionHeader("On another PC?"));
            actions.Controls.Add(InfoLabel("Use Transfer to receive a saved profile from another PC you control."));
            return;
        }

        actions.Controls.Add(ValueLabel(
            selected.Name,
            selected.DisplayName,
            FormatTimestamp(selected.ImportedAt),
            FormatTimestamp(selected.LastPlayedAt),
            selected.CredentialStatus));
        actions.Controls.Add(NewButton("Play in RuneLite", (_, _) => PlaySelected(), BtnStyle.Primary));
        actions.Controls.Add(ButtonRow(
            NewButton("Re-import", (_, _) => ImportCurrent(selected.Name)),
            NewButton("Remove", (_, _) => RemoveSelected(), BtnStyle.Danger)));
        renameProfileBox.Text = selected.Name;
        AddEditableBox("Rename to", renameProfileBox);
        actions.Controls.Add(ButtonRow(
            NewButton("Rename", (_, _) => RenameSelected()),
            NewButton("Open Vault", (_, _) => OpenVaultFolder())));
    }

    private void RenderPairTransfer()
    {
        var selected = SelectedProfileName();
        actions.Controls.Add(SectionHeader("Pair transfer"));
        actions.Controls.Add(ButtonRow(
            NewButton("Send", (_, _) =>
            {
                pairMode = PairMode.Send;
                RenderActions();
            }, pairMode == PairMode.Send ? BtnStyle.Primary : BtnStyle.Default),
            NewButton("Receive", (_, _) =>
            {
                pairMode = PairMode.Receive;
                RenderActions();
            }, pairMode == PairMode.Receive ? BtnStyle.Primary : BtnStyle.Default)));

        if (pairMode == PairMode.Send)
        {
            if (selected is null)
            {
                actions.Controls.Add(InfoLabel("Select a profile before creating pair info."));
                return;
            }

            actions.Controls.Add(InfoLabel("Sends a saved session to another PC you control. The share allows one import, then closes. It also expires after 15 minutes."));

            if (activePairTransfer is null)
            {
                actions.Controls.Add(NewButton("Create Pair", async (_, _) => await StartPairTransfer()));
                return;
            }

            AddReadonlyBox("Pair URL", pairUrlBox);
            AddReadonlyBox("Pair code", pairCodeBox);
            actions.Controls.Add(ButtonRow(
                NewButton("Copy", (_, _) => CopyPairInfo(), BtnStyle.Primary),
                NewButton("Stop Share", (_, _) => StopPairTransfer("Pair transfer stopped."), BtnStyle.Danger)));
            return;
        }

        actions.Controls.Add(InfoLabel("Paste the sender's pair info. The optional name renames it on this PC."));
        AddEditableBox("Pair URL", pairUrlBox);
        AddEditableBox("Pair code", pairCodeBox);
        AddEditableBox("Import as (optional)", receiveNameBox);
        actions.Controls.Add(NewButton("Receive Pair", async (_, _) => await ReceivePair(), BtnStyle.Primary));
    }

    private void RenderAdvanced()
    {
        advancedSettingsBox.Text = "Advanced settings";
        advancedSettingsBox.Width = RightContentWidth;
        advancedSettingsBox.Height = 28;
        advancedSettingsBox.Font = BodyFont;
        advancedSettingsBox.ForeColor = TextColor;
        advancedSettingsBox.BackColor = Bg;
        advancedSettingsBox.Margin = new Padding(0, 18, 0, 8);
        advancedSettingsBox.CheckedChanged -= AdvancedSettingsChanged;
        advancedSettingsBox.CheckedChanged += AdvancedSettingsChanged;
        actions.Controls.Add(advancedSettingsBox);
        advancedPanel.Visible = advancedSettingsBox.Checked;
        actions.Controls.Add(advancedPanel);
    }

    private void AdvancedSettingsChanged(object? sender, EventArgs e)
    {
        advancedPanel.Visible = advancedSettingsBox.Checked;
        UpdateCaptureStatus();
    }

    private void AddProfileNameField(string label)
    {
        AddEditableBox(label, profileNameBox);
    }

    private void AddEditableBox(string label, TextBox box)
    {
        box.ReadOnly = false;
        StyleBox(box);
        actions.Controls.Add(FieldLabel(label));
        actions.Controls.Add(box);
    }

    private void AddReadonlyBox(string label, TextBox box)
    {
        box.ReadOnly = true;
        StyleBox(box);
        actions.Controls.Add(FieldLabel(label));
        actions.Controls.Add(box);
    }

    private static void StyleBox(TextBox box)
    {
        box.Width = RightContentWidth;
        box.BackColor = SurfaceAlt;
        box.ForeColor = TextColor;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.Font = BodyFont;
        box.Margin = new Padding(0, 0, 0, 8);
    }

    private static Button NewButton(string text, EventHandler click, BtnStyle style = BtnStyle.Default)
    {
        var button = new Button
        {
            Text = text,
            Width = RightContentWidth,
            Height = 40,
            FlatStyle = FlatStyle.Flat,
            Font = style == BtnStyle.Primary ? BodyStrongFont : BodyFont,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Surface,
            ForeColor = TextColor,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 0, 6),
            TabStop = true
        };
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = SurfaceAlt;
        button.FlatAppearance.MouseDownBackColor = Border;

        if (style == BtnStyle.Primary)
        {
            button.BackColor = Accent;
            button.ForeColor = AccentText;
            button.FlatAppearance.BorderColor = Accent;
            button.Height = 48;
            button.FlatAppearance.MouseOverBackColor = HighContrast ? SystemColors.Highlight : ControlPaint.Light(Accent, .12f);
            button.FlatAppearance.MouseDownBackColor = HighContrast ? SystemColors.Highlight : ControlPaint.Dark(Accent, .12f);
        }
        else if (style == BtnStyle.Danger)
        {
            button.ForeColor = Danger;
            button.FlatAppearance.BorderColor = Border;
            button.FlatAppearance.MouseOverBackColor = SurfaceAlt;
            button.FlatAppearance.MouseDownBackColor = Border;
        }

        button.Click += click;
        return button;
    }

    private static FlowLayoutPanel ButtonRow(Button left, Button right)
    {
        var each = (RightContentWidth - 6) / 2;
        left.Width = each;
        right.Width = each;
        left.Height = right.Height = 40;
        left.Margin = new Padding(0, 0, 6, 0);
        right.Margin = new Padding(0, 0, 0, 0);
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Width = RightContentWidth,
            Height = 40,
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
            Height = 24,
            Margin = new Padding(0, 16, 0, 8),
            BackColor = Bg
        };
    }

    private static Label FieldLabel(string text)
    {
        return new Label
        {
            Text = text,
            Font = BodyFont,
            ForeColor = Muted,
            Width = RightContentWidth,
            Height = 22,
            Margin = new Padding(0, 2, 0, 2),
            BackColor = Bg
        };
    }

    private static Label InfoLabel(string text)
    {
        return new Label
        {
            Text = text,
            Font = BodyFont,
            ForeColor = Muted,
            Width = RightContentWidth,
            AutoSize = true,
            MaximumSize = new Size(RightContentWidth, 0),
            MinimumSize = new Size(RightContentWidth, 0),
            Margin = new Padding(0, 0, 0, 16),
            BackColor = Bg
        };
    }

    private static Control ValueLabel(string profileName, string character, string imported, string lastPlayed, string credentialStatus)
    {
        var card = new Panel { Width = RightContentWidth, Height = 164, BackColor = Surface,
            Padding = new Padding(16), Margin = new Padding(0, 0, 0, 16) };
        var details = new Label { Text = $"{character}\nImported   {imported}\nLast played   {lastPlayed}",
            Font = BodyFont, ForeColor = Muted, Dock = DockStyle.Fill };
        var title = new Label { Text = profileName, Font = ProfileTitleFont,
            ForeColor = TextColor, Dock = DockStyle.Top, Height = 38, AutoEllipsis = true };
        var status = new Label { Text = credentialStatus, Font = BodyStrongFont,
            ForeColor = credentialStatus == "Ready" ? Accent : Danger, Dock = DockStyle.Bottom, Height = 26 };
        card.Controls.Add(details);
        card.Controls.Add(title);
        card.Controls.Add(status);
        return card;
    }

    private void FillListColumns()
    {
        if (profileList.Columns.Count == 0)
        {
            return;
        }

        var scale = profileList.DeviceDpi / 96f;
        profileList.Columns[1].Width = (int)(132 * scale);
        profileList.Columns[2].Width = (int)(112 * scale);
        profileList.Columns[0].Width = Math.Max(80, profileList.ClientSize.Width
            - profileList.Columns[1].Width - profileList.Columns[2].Width - SystemInformation.VerticalScrollBarWidth - 4);
        profileList.Columns[^1].Width = -2;
        profileList.Invalidate();
    }

    // Win32 owner-draw hover bug: invalidate once per row so DrawSubItem repaints all columns.
    private void OnProfileListMouseMove(object? sender, MouseEventArgs e)
    {
        var item = profileList.GetItemAt(e.X, e.Y);
        if (item is null || hoverRepaintedRows.Contains(item.Index))
        {
            return;
        }

        hoverRepaintedRows.Add(item.Index);
        profileList.Invalidate(FullListClientRowBounds(item.Bounds));
    }

    private void OnProfileListInvalidated(object? sender, InvalidateEventArgs e)
    {
        hoverRepaintedRows.Clear();
    }

    private Rectangle FullListClientRowBounds(Rectangle rowSlice) =>
        new(0, rowSlice.Top, profileList.ClientSize.Width, rowSlice.Height);

    private void OnDrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        if (e.Header is null)
        {
            return;
        }

        if (e.ColumnIndex == 0)
        {
            using var brush = new SolidBrush(Surface);
            e.Graphics.FillRectangle(brush, FullListClientRowBounds(e.Bounds));
        }

        TextRenderer.DrawText(
            e.Graphics,
            e.Header.Text,
            HeaderFont,
            Rectangle.Inflate(e.Bounds, -(int)(12 * profileList.DeviceDpi / 96f), 0),
            Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.LeftAndRightPadding);
    }

    private void OnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item is null || e.Bounds.Width == 0)
        {
            return;
        }

        var scale = profileList.DeviceDpi / 96f;
        if (e.ColumnIndex == 0)
        {
            var bg = e.Item.Selected
                ? RowSelected
                : RowBg;
            using var brush = new SolidBrush(bg);
            e.Graphics.FillRectangle(brush, FullListClientRowBounds(e.Bounds));
            if (e.Item.Selected)
            {
                using var marker = new SolidBrush(Accent);
                e.Graphics.FillRectangle(marker, e.Bounds.Left, e.Bounds.Top + 10 * scale, 3 * scale, e.Bounds.Height - 20 * scale);
            }
        }

        if (e.SubItem is null || e.Item.SubItems.Count <= e.ColumnIndex)
        {
            return;
        }

        var textColor = e.Item.Selected && HighContrast ? SystemColors.HighlightText : TextColor;
        if (!(e.Item.Selected && HighContrast) && e.ColumnIndex == StatusColumnIndex &&
            !string.Equals(e.SubItem.Text, "Ready", StringComparison.Ordinal) &&
            !string.IsNullOrEmpty(e.SubItem.Text))
        {
            textColor = Danger;
        }

        var bounds = Rectangle.Inflate(e.Bounds, -(int)(12 * scale), 0);
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        if (e.ColumnIndex == 0)
        {
            var top = new Rectangle(bounds.X, bounds.Y + (int)(12 * scale), bounds.Width, (int)(25 * scale));
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, BodyStrongFont, top, textColor, flags);
            var bottom = new Rectangle(bounds.X, bounds.Y + (int)(37 * scale), bounds.Width, (int)(23 * scale));
            TextRenderer.DrawText(e.Graphics, (e.Item.Tag as ProfileInfo)?.DisplayName ?? "", BodyFont, bottom,
                e.Item.Selected && HighContrast ? SystemColors.HighlightText : Muted, flags);
            if (e.Item.Focused && profileList.Focused)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(e.Bounds, -4, -4), textColor, RowSelected);
        }
        else
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, BodyFont, bounds, textColor, flags);

    }

    private void RefreshProfiles()
    {
        RunAction(() => LoadProfiles());
    }

    private void LoadProfiles(string? selectName = null)
    {
        var targetSelection = selectName ?? SelectedProfileName();
        profileList.BeginUpdate();
        try
        {
            profileList.Items.Clear();
            var profiles = switcher.GetProfiles();
            foreach (var profile in profiles)
            {
                var item = new ListViewItem(profile.Name);
                item.SubItems.Add(FormatTimestamp(profile.LastPlayedAt));
                item.SubItems.Add(profile.CredentialStatus);
                item.Tag = profile;
                profileList.Items.Add(item);
            }

            if (profileList.Items.Count > 0)
            {
                var selected = profileList.Items
                    .Cast<ListViewItem>()
                    .FirstOrDefault(item => string.Equals((item.Tag as ProfileInfo)?.Name, targetSelection, StringComparison.OrdinalIgnoreCase))
                    ?? profileList.Items[0];
                selected.Selected = true;
                selected.Focused = true;
                selected.EnsureVisible();
            }

            libraryCount.Text = $"YOUR PROFILES   /   {profiles.Count:00}";
            emptyPanel.Visible = profiles.Count == 0;
            profileList.Visible = profiles.Count > 0;
            SetStatus(profiles.Count == 0 ? "No profiles saved." : $"{profiles.Count} profile(s).");
        }
        finally
        {
            profileList.EndUpdate();
        }

        RenderActions();
        FillListColumns();
    }

    private void ImportCurrent(string? forcedName = null)
    {
        var profileName = (forcedName ?? profileNameBox.Text).Trim();
        RunAction(() =>
        {
            if (!string.IsNullOrWhiteSpace(profileName) &&
                ProfileExists(profileName) &&
                !Confirm($"Replace saved credentials for '{profileName}'?"))
            {
                return;
            }

            var importedName = switcher.Import(profileName);
            profileNameBox.Clear();
            actionPage = ActionPage.Profile;
            LoadProfiles(importedName);
            SetStatus($"Imported profile '{importedName}'.");
        });
    }

    private void StartAddAccount()
    {
        var profileName = profileNameBox.Text.Trim();
        RunAction(() =>
        {
            if (!string.IsNullOrWhiteSpace(profileName) &&
                ProfileExists(profileName) &&
                !Confirm($"Replace saved credentials for '{profileName}' after capture?"))
            {
                return;
            }

            if (switcher.IsRuneLiteRunning())
            {
                throw new InvalidOperationException("Close RuneLite before adding an account so the app can track the new Jagex-launched client.");
            }

            if (switcher.IsOfficialOldSchoolClientRunning())
            {
                throw new InvalidOperationException("Close the official Old School client first. Add Account needs RuneLite.");
            }

            addAccountActive = true;
            captureStableTicks = 0;
            pendingProfileName = profileName;
            SetAddStep("Opening Jagex Launcher...");
            switcher.BeginAddAccount();
            addAccountTimer.Start();
            RenderActions();
            SetStatus("Jagex Launcher opened. Pick RuneLite, then start the character.");
        });
    }

    private void WatchAddAccount()
    {
        if (!addAccountActive || pendingProfileName is null)
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
                SetAddStep("Waiting for Jagex Launcher...");
                return;
            }

            if (!runeLiteRunning)
            {
                SetAddStep("Jagex Launcher detected. Start RuneLite for the character.");
                return;
            }

            if (!switcher.HasCapturedCredentials())
            {
                captureStableTicks = 0;
                SetAddStep("RuneLite detected. Waiting for captured credentials...");
                return;
            }

            if (++captureStableTicks < 2)
            {
                SetAddStep("Credentials detected. Verifying...");
                return;
            }

            var importedName = switcher.CompleteAddAccount(pendingProfileName);
            profileNameBox.Clear();
            actionPage = ActionPage.Profile;
            LoadProfiles(importedName);
            StopAddAccount($"Added profile '{importedName}'. Capture mode turned off.");
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
        addAccountActive = false;
        captureStableTicks = 0;
        pendingProfileName = null;
        RenderActions();

        if (!string.IsNullOrWhiteSpace(message))
        {
            SetStatus(message);
        }
    }

    private void SetAddStep(string text)
    {
        addStepLabel.Text = text;
        SetStatus(text);
    }

    private void PlaySelected()
    {
        var profileName = SelectedProfileName();
        if (profileName is null)
        {
            return;
        }

        RunAction(() =>
        {
            if (switcher.IsCaptureEnabled() && !advancedSettingsBox.Checked)
            {
                switcher.SetCaptureEnabled(false);
            }

            switcher.Play(profileName);
            LoadProfiles(profileName);
            SetStatus($"Playing profile '{profileName}'.");
        });
    }

    private void RemoveSelected()
    {
        var profileName = SelectedProfileName();
        if (profileName is null)
        {
            return;
        }

        if (!Confirm($"Remove profile '{profileName}'?"))
        {
            return;
        }

        RunAction(() =>
        {
            switcher.Remove(profileName);
            LoadProfiles();
            SetStatus($"Removed profile '{profileName}'.");
        });
    }

    private void RenameSelected()
    {
        var profileName = SelectedProfileName();
        if (profileName is null)
        {
            return;
        }

        var newName = renameProfileBox.Text.Trim();
        if (string.Equals(profileName, newName, StringComparison.Ordinal))
        {
            SetStatus("Name unchanged.");
            return;
        }

        RunAction(() =>
        {
            switcher.RenameProfile(profileName, newName);
            LoadProfiles(newName);
            SetStatus($"Renamed profile '{profileName}' to '{newName}'.");
        });
    }

    private void OpenVaultFolder()
    {
        RunAction(() =>
        {
            var path = switcher.GetVaultRoot();
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
            SetStatus("Opened vault folder.");
        });
    }

    private void ForgetAllProfiles()
    {
        var path = switcher.GetVaultRoot();
        if (!Confirm($"Forget all saved profiles and credentials in {path}?"))
        {
            return;
        }

        RunAction(() =>
        {
            activePairTransfer?.Dispose();
            activePairTransfer = null;
            switcher.ForgetAllProfiles();
            profileNameBox.Clear();
            renameProfileBox.Clear();
            pairUrlBox.Clear();
            pairCodeBox.Clear();
            receiveNameBox.Clear();
            LoadProfiles();
            SetStatus("Forgot all saved profiles.");
        });
    }

    private async Task StartPairTransfer()
    {
        var profileName = SelectedProfileName();
        if (profileName is null)
        {
            return;
        }

        await RunActionAsync(async () =>
        {
            activePairTransfer?.Dispose();
            activePairTransfer = null;
            pairMode = PairMode.Send;
            SetStatus("Preparing Cloudflare tunnel (first use downloads cloudflared)...");
            RenderActions();

            var session = await switcher.StartPairTransferAsync(profileName);
            activePairTransfer = session;
            session.Closed += reason => BeginInvoke((Action)(() =>
            {
                if (activePairTransfer == session)
                {
                    StopPairTransfer(reason);
                }
            }));

            pairUrlBox.Text = session.TunnelUrl;
            pairCodeBox.Text = session.Code;
            CopyPairInfo();
            RenderActions();
            SetStatus("Pair info copied. Keep this app open until the other PC imports it.");
        });
    }

    private async Task ReceivePair()
    {
        var pairUrl = pairUrlBox.Text.Trim();
        var pairCode = pairCodeBox.Text.Trim();
        var profileName = receiveNameBox.Text.Trim();

        await RunActionAsync(async () =>
        {
            SetStatus("Contacting sender...");
            var importedName = await switcher.ReceivePairAsync(pairUrl, pairCode, profileName);
            receiveNameBox.Clear();
            pairUrlBox.Clear();
            pairCodeBox.Clear();
            actionPage = ActionPage.Profile;
            LoadProfiles(importedName);
            SetStatus($"Imported paired profile '{importedName}'.");
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
        SetStatus("Pair info copied.");
    }

    private void StopPairTransfer(string message)
    {
        activePairTransfer?.Dispose();
        activePairTransfer = null;
        pairUrlBox.Clear();
        pairCodeBox.Clear();
        RenderActions();
        SetStatus(message);
    }

    private bool ProfileExists(string profileName)
    {
        return profileList.Items
            .Cast<ListViewItem>()
            .Any(item => string.Equals((item.Tag as ProfileInfo)?.Name, profileName, StringComparison.OrdinalIgnoreCase));
    }

    private ProfileInfo? SelectedProfile()
    {
        return profileList.SelectedItems.Count == 0
            ? null
            : profileList.SelectedItems[0].Tag as ProfileInfo;
    }

    private string? SelectedProfileName()
    {
        return SelectedProfile()?.Name;
    }

    private void SetStatus(string message)
    {
        statusLabel.Text = message;
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
        if (busy)
        {
            return;
        }

        busy = true;
        UseWaitCursor = true;
        try
        {
            await action();
            UpdateCaptureStatus();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            busy = false;
            UseWaitCursor = false;
        }
    }

    private void UpdateCaptureStatus()
    {
        var captureOn = switcher.IsCaptureEnabled();
        captureLabel.Visible = captureOn;
        if (captureLabel.Parent is not null)
            captureLabel.Parent.Height = (int)((captureOn ? 122 : 96) * DeviceDpi / 96f);
        captureLabel.Text = captureOn ? "Capture mode is on. Normal Play turns it off unless Advanced settings is open." : "";
    }

    private static bool Confirm(string message)
    {
        return MessageBox.Show(
            message,
            "Jagex Switcher",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning) == DialogResult.Yes;
    }

    private static void ShowError(string message)
    {
        MessageBox.Show(message, "Jagex Switcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static string FormatTimestamp(string value)
    {
        if (!DateTimeOffset.TryParse(value, out var timestamp))
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }

        var local = timestamp.ToLocalTime();
        var now = DateTimeOffset.Now;
        if (local.Date == now.Date)
        {
            return $"Today {local:HH:mm}";
        }

        if (local.Date == now.AddDays(-1).Date)
        {
            return $"Yesterday {local:HH:mm}";
        }

        return local.Year == now.Year
            ? local.ToString("MMM d, HH:mm")
            : local.ToString("yyyy-MM-dd HH:mm");
    }

    private static bool IsWindowsLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value > 0;
        }
        catch
        {
            return false;
        }
    }
}
