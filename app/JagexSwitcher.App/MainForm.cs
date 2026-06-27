namespace JagexSwitcher.App;

internal sealed class MainForm : Form
{
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
        MinimumSize = new Size(760, 460);
        Size = new Size(820, 500);

        BuildLayout();
        addAccountTimer.Interval = 1000;
        addAccountTimer.Tick += (_, _) => WatchAddAccount();
        FormClosed += (_, _) => activePairTransfer?.Dispose();
        RefreshProfiles();
    }

    private void BuildLayout()
    {
        profileList.View = View.Details;
        profileList.FullRowSelect = true;
        profileList.MultiSelect = false;
        profileList.HideSelection = false;
        profileList.Dock = DockStyle.Fill;
        profileList.Columns.Add("Profile", 140);
        profileList.Columns.Add("Display", 160);
        profileList.Columns.Add("Imported", 230);
        profileList.DoubleClick += (_, _) => PlaySelected();

        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(10)
        };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(10, 0, 0, 0)
        };

        var nameLabel = new Label
        {
            Text = "Profile name",
            Width = 200,
            Height = 22
        };

        profileNameBox.Width = 200;
        profileNameBox.Margin = new Padding(0, 0, 0, 10);

        actions.Controls.Add(nameLabel);
        actions.Controls.Add(profileNameBox);
        actions.Controls.Add(NewButton("Add Account", (_, _) => StartAddAccount()));
        cancelAddButton = NewButton("Cancel Add", (_, _) => StopAddAccount("Account add cancelled."));
        cancelAddButton.Enabled = false;
        cancelAddButton.Visible = false;
        actions.Controls.Add(cancelAddButton);
        actions.Controls.Add(Spacer());
        actions.Controls.Add(NewButton("Add Current", (_, _) => ImportCurrent()));
        actions.Controls.Add(NewButton("Play Selected", (_, _) => PlaySelected()));
        actions.Controls.Add(NewButton("Remove Selected", (_, _) => RemoveSelected()));
        actions.Controls.Add(NewButton("Refresh", (_, _) => RefreshProfiles()));
        actions.Controls.Add(Spacer());
        actions.Controls.Add(NewLabel("Pair URL"));
        pairUrlBox.Width = 200;
        pairUrlBox.Margin = new Padding(0, 0, 0, 8);
        actions.Controls.Add(pairUrlBox);
        actions.Controls.Add(NewLabel("Pair code"));
        pairCodeBox.Width = 200;
        pairCodeBox.Margin = new Padding(0, 0, 0, 10);
        actions.Controls.Add(pairCodeBox);
        actions.Controls.Add(NewButton("Send Selected", async (_, _) => await StartPairTransfer()));
        actions.Controls.Add(NewButton("Receive Pair", async (_, _) => await ReceivePair()));
        actions.Controls.Add(NewButton("Copy Pair Info", (_, _) => CopyPairInfo()));
        stopPairButton = NewButton("Stop Share", (_, _) => StopPairTransfer("Pair transfer stopped."));
        stopPairButton.Enabled = false;
        stopPairButton.Visible = false;
        actions.Controls.Add(stopPairButton);

        advancedSettingsBox.Text = "Advanced settings";
        advancedSettingsBox.Width = 200;
        advancedSettingsBox.Height = 28;
        advancedSettingsBox.Margin = new Padding(0, 14, 0, 8);
        advancedSettingsBox.CheckedChanged += (_, _) => UpdateAdvancedUi();
        actions.Controls.Add(advancedSettingsBox);

        advancedPanel.FlowDirection = FlowDirection.TopDown;
        advancedPanel.WrapContents = false;
        advancedPanel.AutoSize = true;
        advancedPanel.Width = 210;
        advancedPanel.Visible = false;
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

        var status = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1
        };
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        captureLabel.Dock = DockStyle.Fill;
        statusLabel.Dock = DockStyle.Fill;
        status.Controls.Add(captureLabel, 0, 0);
        status.Controls.Add(statusLabel, 1, 0);

        main.Controls.Add(profileList, 0, 0);
        main.Controls.Add(actions, 1, 0);
        main.Controls.Add(status, 0, 1);
        main.SetColumnSpan(status, 2);

        Controls.Add(main);
    }

    private static Button NewButton(string text, EventHandler click)
    {
        var button = new Button
        {
            Text = text,
            Width = 200,
            Height = 32,
            Margin = new Padding(0, 0, 0, 8)
        };
        button.Click += click;
        return button;
    }

    private static Label NewLabel(string text)
    {
        return new Label
        {
            Text = text,
            Width = 200,
            Height = 22
        };
    }

    private static Label Spacer()
    {
        return new Label
        {
            Width = 200,
            Height = 8
        };
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
        captureLabel.Text = advancedSettingsBox.Checked
            ? switcher.IsCaptureEnabled() ? "Capture: on" : "Capture: off"
            : "";
    }

    private static void ShowError(string message)
    {
        MessageBox.Show(message, "Jagex Switcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
