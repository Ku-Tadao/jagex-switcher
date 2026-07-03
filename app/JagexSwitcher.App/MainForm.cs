namespace JagexSwitcher.App;

internal sealed class MainForm : Form
{
    // ponytail: static palette, no theme engine. Add a full theme model only if users ask for custom themes.
    // Ceiling: reads Windows theme at startup only. Upgrade path: listen for WM_SETTINGCHANGE.
    private static readonly bool HighContrast = SystemInformation.HighContrast;
    private static readonly bool LightTheme = !HighContrast && IsWindowsLightTheme();
    private static readonly Color Bg = HighContrast ? SystemColors.Window : LightTheme ? Color.FromArgb(248, 247, 244) : Color.FromArgb(27, 27, 41);
    private static readonly Color Surface = HighContrast ? SystemColors.Control : LightTheme ? Color.FromArgb(238, 236, 230) : Color.FromArgb(38, 38, 56);
    private static readonly Color SurfaceAlt = HighContrast ? SystemColors.ControlLight : LightTheme ? Color.FromArgb(244, 242, 237) : Color.FromArgb(34, 34, 52);
    private static readonly Color Border = HighContrast ? SystemColors.WindowText : LightTheme ? Color.FromArgb(190, 185, 176) : Color.FromArgb(51, 51, 74);
    private static readonly Color TextColor = HighContrast ? SystemColors.WindowText : LightTheme ? Color.FromArgb(31, 31, 38) : Color.FromArgb(232, 232, 240);
    private static readonly Color Muted = HighContrast ? SystemColors.GrayText : LightTheme ? Color.FromArgb(86, 84, 96) : Color.FromArgb(158, 158, 184);
    private static readonly Color Accent = HighContrast ? SystemColors.Highlight : LightTheme ? Color.FromArgb(174, 113, 31) : Color.FromArgb(232, 177, 75);
    private static readonly Color AccentText = HighContrast ? SystemColors.HighlightText : LightTheme ? Color.FromArgb(255, 252, 244) : Color.FromArgb(27, 27, 41);
    private static readonly Color Danger = HighContrast ? SystemColors.HotTrack : Color.FromArgb(224, 85, 107);
    private static readonly Color RowBg = HighContrast ? SystemColors.Window : LightTheme ? Color.FromArgb(253, 252, 248) : Color.FromArgb(34, 34, 52);
    private static readonly Color RowBgAlt = HighContrast ? SystemColors.Window : LightTheme ? Color.FromArgb(246, 244, 239) : Color.FromArgb(30, 30, 46);
    private static readonly Color RowSelected = HighContrast ? SystemColors.Highlight : LightTheme ? Color.FromArgb(247, 221, 173) : Color.FromArgb(58, 51, 32);
    private static readonly Color WarningBg = HighContrast ? SystemColors.Control : LightTheme ? Color.FromArgb(255, 238, 197) : Color.FromArgb(61, 48, 30);

    private static readonly Font BodyFont = new("Segoe UI", 9F);
    private static readonly Font BodyStrongFont = new("Segoe UI Semibold", 9F);
    private static readonly Font TitleFont = new("Segoe UI Semibold", 15F);
    private static readonly Font SectionFont = new("Segoe UI", 8F, FontStyle.Bold);
    private static readonly Font HeaderFont = new("Segoe UI", 8.25F, FontStyle.Bold);

    private const int RightColumnWidth = 360;
    private const int RightContentWidth = 296;
    private const int StatusColumnIndex = 4;

    private readonly SwitcherService switcher;
    private readonly ListView profileList = new();
    private readonly Panel profileHost = new();
    private readonly Panel emptyPanel = new();
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
    private bool addAccountActive;
    private bool busy;
    private int captureStableTicks;
    private string? pendingProfileName;
    private SwitcherService.PairTransferSession? activePairTransfer;
    private readonly HashSet<int> hoverRepaintedRows = new();

    private enum BtnStyle { Default, Primary, Danger }
    private enum PairMode { Send, Receive }

    public MainForm(SwitcherService switcher)
    {
        this.switcher = switcher;

        var version = typeof(MainForm).Assembly.GetName().Version;
        Text = version is null || (version.Major == 0 && version.Minor == 0 && version.Build == 0)
            ? "Jagex Switcher"
            : $"Jagex Switcher v{version.ToString(3)}";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(860, 560);
        Size = new Size(1040, 720);
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

        actions.Dock = DockStyle.Fill;
        actions.FlowDirection = FlowDirection.TopDown;
        actions.WrapContents = false;
        actions.AutoScroll = true;
        actions.BackColor = Bg;
        actions.Padding = new Padding(18, 8, 0, 8);

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
        body.Controls.Add(profileHost, 0, 0);
        body.Controls.Add(actions, 1, 0);

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
        profileList.DrawColumnHeader += OnDrawColumnHeader;
        profileList.DrawSubItem += OnDrawSubItem;
        profileList.SelectedIndexChanged += (_, _) => RenderActions();
        profileList.ClientSizeChanged += (_, _) => FillListColumns();
        // ponytail: MS OwnerDraw workaround — Win32 can fire DrawItem without DrawSubItem on hover.
        profileList.MouseMove += OnProfileListMouseMove;
        profileList.Invalidated += OnProfileListInvalidated;
        profileList.DoubleClick += (_, _) => PlaySelected();
        profileList.ContextMenuStrip = BuildProfileContextMenu();

        typeof(Control)
            .GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(profileList, true);

        profileList.Columns.Add("Profile", 140);
        profileList.Columns.Add("Character", 150);
        profileList.Columns.Add("Imported", 120);
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
        emptyPanel.BackColor = RowBg;
        emptyPanel.Padding = new Padding(28);

        var title = new Label
        {
            Text = "No saved profiles yet",
            Dock = DockStyle.Top,
            Height = 32,
            Font = new Font("Segoe UI Semibold", 13F),
            ForeColor = TextColor,
            BackColor = RowBg
        };
        var copy = new Label
        {
            Text = "Name the profile on the right, then use Add Account for the guided Jagex Launcher capture. Use Add Current only when RuneLite already wrote credentials.",
            Dock = DockStyle.Top,
            Height = 60,
            Font = BodyFont,
            ForeColor = Muted,
            BackColor = RowBg
        };

        emptyPanel.Controls.Add(copy);
        emptyPanel.Controls.Add(title);
    }

    private Control BuildHeader()
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 72,
            BackColor = Surface
        };

        captureLabel.Dock = DockStyle.Bottom;
        captureLabel.Height = 18;
        captureLabel.Font = BodyFont;
        captureLabel.ForeColor = Accent;
        captureLabel.BackColor = WarningBg;
        captureLabel.Padding = new Padding(16, 1, 0, 0);
        captureLabel.Visible = false;

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
            Font = TitleFont,
            ForeColor = TextColor,
            Dock = DockStyle.Top,
            Height = 34,
            Padding = new Padding(16, 8, 0, 0),
            BackColor = Surface
        };
        var accentStripe = new Panel
        {
            Dock = DockStyle.Top,
            Height = 3,
            BackColor = Accent
        };

        header.Controls.Add(captureLabel);
        header.Controls.Add(subtitle);
        header.Controls.Add(title);
        header.Controls.Add(accentStripe);
        return header;
    }

    private Control BuildStatusBar()
    {
        var statusBar = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 32,
            BackColor = Surface
        };

        statusLabel.Dock = DockStyle.Fill;
        statusLabel.Font = BodyFont;
        statusLabel.ForeColor = TextColor;
        statusLabel.Padding = new Padding(16, 7, 16, 0);
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
            RenderProfileActions();
            RenderPairTransfer();
            RenderAdvanced();
        }

        actions.ResumeLayout();
        UpdateCaptureStatus();
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
            actions.Controls.Add(InfoLabel("Create the first saved profile from a Jagex-launched RuneLite session. Name is optional; blank uses the captured character name."));
            AddProfileNameField("Profile name (optional)");
            actions.Controls.Add(NewButton("Add Account", (_, _) => StartAddAccount(), BtnStyle.Primary));
            actions.Controls.Add(NewButton("Add Current", (_, _) => ImportCurrent()));
            actions.Controls.Add(NewButton("Refresh", (_, _) => RefreshProfiles()));
            return;
        }

        actions.Controls.Add(ValueLabel(
            selected.Name,
            selected.DisplayName,
            FormatTimestamp(selected.ImportedAt),
            FormatTimestamp(selected.LastPlayedAt),
            selected.CredentialStatus));
        actions.Controls.Add(NewButton("Play", (_, _) => PlaySelected(), BtnStyle.Primary));
        actions.Controls.Add(ButtonRow(
            NewButton("Re-import", (_, _) => ImportCurrent(selected.Name)),
            NewButton("Remove", (_, _) => RemoveSelected(), BtnStyle.Danger)));
        renameProfileBox.Text = selected.Name;
        AddEditableBox("Rename to", renameProfileBox);
        actions.Controls.Add(ButtonRow(
            NewButton("Rename", (_, _) => RenameSelected()),
            NewButton("Open Vault", (_, _) => OpenVaultFolder())));
        actions.Controls.Add(NewButton("Refresh", (_, _) => RefreshProfiles()));
        actions.Controls.Add(SectionHeader("Add another"));
        AddProfileNameField("Profile name (optional)");
        actions.Controls.Add(ButtonRow(
            NewButton("Add Account", (_, _) => StartAddAccount()),
            NewButton("Add Current", (_, _) => ImportCurrent())));
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
            Height = 32,
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
            Margin = new Padding(0, 14, 0, 6),
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
            Height = 18,
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
            Height = 50,
            Margin = new Padding(0, 0, 0, 8),
            BackColor = Bg
        };
    }

    private static Label ValueLabel(string profileName, string character, string imported, string lastPlayed, string credentialStatus)
    {
        return new Label
        {
            Text = $"{profileName}{Environment.NewLine}{character}{Environment.NewLine}Imported {imported}{Environment.NewLine}Last played {lastPlayed}{Environment.NewLine}Status {credentialStatus}",
            Font = BodyFont,
            ForeColor = TextColor,
            Width = RightContentWidth,
            Height = 96,
            Margin = new Padding(0, 0, 0, 8),
            BackColor = Bg
        };
    }

    private void FillListColumns()
    {
        if (profileList.Columns.Count == 0)
        {
            return;
        }

        var used = 0;
        for (var i = 0; i < profileList.Columns.Count - 1; i++)
        {
            used += profileList.Columns[i].Width;
        }

        var remaining = profileList.ClientSize.Width - used;
        profileList.Columns[^1].Width = remaining > 0 ? remaining : 0;
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

    private Rectangle FullListClientRowBounds(Rectangle rowSlice)
    {
        var width = profileList.ClientSize.Width - rowSlice.Left;
        if (HasVerticalScrollBar())
        {
            width -= SystemInformation.VerticalScrollBarWidth;
        }

        return new Rectangle(rowSlice.Left, rowSlice.Top, Math.Max(rowSlice.Width, width), rowSlice.Height);
    }

    private bool HasVerticalScrollBar()
    {
        if (profileList.Items.Count == 0)
        {
            return false;
        }

        var itemHeight = profileList.GetItemRect(0, ItemBoundsPortion.Entire).Height;
        return itemHeight > 0 && profileList.Items.Count * itemHeight > profileList.ClientSize.Height;
    }

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
            e.Bounds,
            Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.LeftAndRightPadding);
    }

    private void OnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item is null)
        {
            return;
        }

        if (e.ColumnIndex == 0)
        {
            var bg = e.Item.Selected
                ? RowSelected
                : ((e.ItemIndex & 1) == 0 ? RowBg : RowBgAlt);
            using var brush = new SolidBrush(bg);
            e.Graphics.FillRectangle(brush, FullListClientRowBounds(e.Bounds));
        }

        if (e.SubItem is null || e.Item.SubItems.Count <= e.ColumnIndex)
        {
            return;
        }

        var textColor = e.Item.Selected && HighContrast ? SystemColors.HighlightText : TextColor;
        if (e.ColumnIndex == StatusColumnIndex &&
            !string.Equals(e.SubItem.Text, "Ready", StringComparison.Ordinal) &&
            !string.IsNullOrEmpty(e.SubItem.Text))
        {
            textColor = Danger;
        }

        TextRenderer.DrawText(
            e.Graphics,
            e.SubItem.Text,
            BodyFont,
            e.Bounds,
            textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.LeftAndRightPadding);
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
                item.SubItems.Add(string.IsNullOrWhiteSpace(profile.DisplayName) ? "-" : profile.DisplayName);
                item.SubItems.Add(FormatTimestamp(profile.ImportedAt));
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

            emptyPanel.Visible = profiles.Count == 0;
            profileList.Visible = profiles.Count > 0;
            SetStatus(profiles.Count == 0 ? "No profiles saved." : $"{profiles.Count} profile(s).");
        }
        finally
        {
            profileList.EndUpdate();
        }

        RenderActions();
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
