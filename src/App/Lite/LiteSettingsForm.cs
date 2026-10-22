// MwbLite: small Settings window that edits %LOCALAPPDATA%\MwbLite\settings.json.
//
// It edits a copy of the loaded config, so values that are not on the form are preserved, validates with
// the same LiteConfig.Validate used by "Reload config", and writes the file only when you press Save.
// The window always runs as the interactive user: in service mode the SYSTEM tray starts
// "MwbLite.exe --settings" with the user's token (see LiteTray.ShowSettings).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.Lite
{
    internal sealed class LiteSettingsForm : System.Windows.Forms.Form
    {
        private static readonly (string Label, EasyMouseOption Value)[] EasyMouseChoices =
        {
            ("Enabled: cross screen edges freely", EasyMouseOption.Enable),
            ("Hold Ctrl to cross an edge", EasyMouseOption.Ctrl),
            ("Hold Shift to cross an edge", EasyMouseOption.Shift),
            ("Disabled: switch with hotkeys only", EasyMouseOption.Disable),
        };

        private static readonly (string Label, string Value)[] SwitchChoices =
        {
            ("Ctrl+Alt+F1 … F4", "F1"),
            ("Ctrl+Alt+1 … 4", "1"),
            ("Disabled", "disabled"),
        };

        private readonly string settingsPath;
        private LiteConfig loaded;
        private readonly Action<LiteConfig> afterSave;
        private readonly int em;

        private readonly TextBox keyBox = new();
        private readonly CheckBox showKey = new();
        private readonly LayoutEditor layoutEditor = new();
        private readonly RadioButton oneRow = new();
        private readonly RadioButton twoRows = new();
        private readonly CheckBox wrapMouse = new();
        private readonly CheckBox shareClipboard = new();
        private readonly CheckBox transferFile = new();
        private readonly CheckBox showTransferProgress = new();
        private readonly CheckBox startWithWindows = new();
        private readonly CheckBox useService = new();
        private readonly GroupBox advancedGroup = new();
        private readonly LinkLabel advancedToggle = new();
        private readonly NumericUpDown tcpPort = new();
        private readonly ComboBox easyMouse = new();
        private readonly ComboBox switchMachine = new();
        private readonly TextBox hkToggleEasyMouse = new();
        private readonly TextBox hkLockMachine = new();
        private readonly TextBox hkReconnect = new();
        private readonly TextBox hkSwitchToAll = new();
        private readonly TextBox name2Ip = new();
        private readonly CheckBox sameSubnetOnly = new();
        private readonly CheckBox validateRemoteIp = new();
        private readonly CheckBox hideMouseAtEdge = new();
        private readonly CheckBox drawMouseCursor = new();
        private readonly CheckBox blockScreenSaver = new();
        private readonly CheckBox moveMouseRelatively = new();
        private readonly CheckBox blockMouseAtCorners = new();
        private readonly CheckBox showStatusMessages = new();
        private readonly CheckBox noEasyMouseFullscreen = new();
        private readonly CheckBox allowInsecure = new();
        private readonly Label status = new();
        private bool commentsAcknowledged;

        private LiteSettingsForm(string settingsPath, LiteConfig config, Action<LiteConfig> afterSave)
        {
            this.settingsPath = settingsPath;
            loaded = config;
            this.afterSave = afterSave;

            Font = SystemFonts.MessageBoxFont;
            em = TextRenderer.MeasureText("M", Font).Width;

            Text = "MwbLite Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            ShowInTaskbar = true;
            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch (Exception)
            {
                // Keep the default icon.
            }

            BuildLayout();
            LoadValues(config);
            _ = HasUnsavedChanges(); // capture the baseline
        }

        /// <summary>Loads settings.json (creating it if missing) and returns the window, or null if the user cancelled.</summary>
        internal static LiteSettingsForm Create(string settingsPath, Action<LiteConfig> afterSave)
        {
            try
            {
                _ = LiteConfig.WriteDefaultIfMissing(settingsPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LiteLog.Write(nameof(LiteSettingsForm), "Cannot create " + settingsPath + ": " + e.Message);
            }

            var cfg = LiteConfig.TryParse(settingsPath, out string error);
            if (cfg == null)
            {
                var answer = MessageBox.Show(
                    error + "\r\n\r\nYes: open settings.json in Notepad to fix it.\r\nNo: start from default settings (your file is kept as settings.json.bak when you save).",
                    "MwbLite Settings",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Warning);

                if (answer == DialogResult.Yes)
                {
                    LiteTray.OpenUserConfigInEditor();
                    return null;
                }

                if (answer != DialogResult.No)
                {
                    return null;
                }

                cfg = LiteConfig.CreateDefault();
            }

            return new LiteSettingsForm(settingsPath, cfg, afterSave);
        }

        // ---- layout ---------------------------------------------------------------------------

        private void BuildLayout()
        {
            var root = new TableLayoutPanel
            {
                ColumnCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(em),
                Dock = DockStyle.Fill,
            };

            root.Controls.Add(BuildPairingGroup());
            root.Controls.Add(BuildMachinesGroup());
            root.Controls.Add(BuildOptionsGroup());

            advancedToggle.Text = "Show advanced settings";
            advancedToggle.AutoSize = true;
            advancedToggle.Margin = new Padding(3, em / 2, 3, 3);
            advancedToggle.LinkClicked += (s, e) => SetAdvancedVisible(!advancedGroup.Visible);
            root.Controls.Add(advancedToggle);

            root.Controls.Add(BuildAdvancedGroup());
            advancedGroup.Visible = false;

            status.AutoSize = true;
            status.MaximumSize = new Size(em * 40, 0);
            status.ForeColor = Color.Firebrick;
            status.Visible = false;
            status.Margin = new Padding(3, em / 2, 3, 0);
            root.Controls.Add(status);

            root.Controls.Add(BuildButtons());
            Controls.Add(root);
        }

        private GroupBox BuildPairingGroup()
        {
            var grid = NewGrid(2);

            grid.Controls.Add(NewLabel("This PC:"));
            grid.Controls.Add(new Label { Text = LiteConfig.NormalizeName(Environment.MachineName), AutoSize = true, Font = new Font(Font, FontStyle.Bold), Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) });

            grid.Controls.Add(NewLabel("Pair a PC:"));
            var pairRow = NewFlow();
            pairRow.Controls.Add(NewButton("Add a PC…", (s, e) => RunPairing(host: true)));
            pairRow.Controls.Add(NewButton("Join…", (s, e) => RunPairing(host: false)));
            grid.Controls.Add(pairRow);

            grid.Controls.Add(new Label());
            grid.Controls.Add(NewHint("Press Add a PC… here and Join… on the new PC; both show a code to compare. The new PC gets this group's security key and layout."));

            return NewGroup("Pairing", grid);
        }

        // The shared security key: set by pairing, so it lives under Advanced (see README "How pairing works").
        private Control BuildKeyRow()
        {
            var keyRow = NewFlow();
            keyBox.Width = em * 16;
            keyBox.Font = new Font(FontFamily.GenericMonospace, Font.Size);
            keyBox.UseSystemPasswordChar = true;
            showKey.Text = "Show";
            showKey.AutoSize = true;
            showKey.CheckedChanged += (s, e) => keyBox.UseSystemPasswordChar = !showKey.Checked;
            var reset = NewButton("Reset pairing…", (s, e) =>
            {
                if (keyBox.Text.Length == 0 || MessageBox.Show(
                    this,
                    "Create a new security key for this PC?\r\n\r\nAfter you save, this PC stops working with the others until you pair them again (Add a PC… / Join…). Use this to remove a PC from your group.",
                    "Reset pairing",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning) == DialogResult.OK)
                {
                    keyBox.Text = Encryption.CreateRandomKey();
                    showKey.Checked = true;
                }
            });
            var copy = NewButton("Copy", (s, e) =>
            {
                if (keyBox.Text.Length > 0)
                {
                    System.Windows.Forms.Clipboard.SetText(keyBox.Text);
                    SetStatus("Key copied to the clipboard.", ok: true);
                }
            });
            keyRow.Controls.AddRange(new Control[] { keyBox, showKey, copy, reset });
            return keyRow;
        }

        private GroupBox BuildMachinesGroup()
        {
            var outer = NewGrid(1);

            var modeRow = NewFlow();
            oneRow.Text = "One row";
            oneRow.AutoSize = true;
            twoRows.Text = "Two rows (2 × 2)";
            twoRows.AutoSize = true;
            wrapMouse.Text = "Wrap the cursor around the outer edges";
            wrapMouse.AutoSize = true;
            wrapMouse.Margin = new Padding(em, 3, 3, 3);
            oneRow.CheckedChanged += (s, e) => layoutEditor.OneRow = oneRow.Checked;
            modeRow.Controls.AddRange(new Control[] { oneRow, twoRows, wrapMouse });
            outer.Controls.Add(modeRow);

            layoutEditor.Font = Font;
            layoutEditor.ThisPc = LiteConfig.NormalizeName(Environment.MachineName);
            layoutEditor.Margin = new Padding(3, em / 2, 3, em / 2);
            outer.Controls.Add(layoutEditor);

            var helpRow = NewFlow();
            var addThis = new LinkLabel { Text = "Add this PC", AutoSize = true, Margin = new Padding(3, 3, em, 3) };
            addThis.LinkClicked += (s, e) => AddThisPc();
            helpRow.Controls.Add(addThis);
            helpRow.Controls.Add(NewHint("Drag the PCs to match where they sit. Double-click to rename or add, Delete to remove. Other PCs follow when you save."));
            outer.Controls.Add(helpRow);

            return NewGroup("Machines and layout", outer);
        }

        private GroupBox BuildOptionsGroup()
        {
            var grid = NewGrid(1);
            shareClipboard.Text = "Share the clipboard (text and images)";
            transferFile.Text = "Transfer files and folders (copy/paste up to 100 MB in total; drag and drop any size)";
            transferFile.Margin = new Padding(em * 2, 3, 3, 3);
            showTransferProgress.Text = "Show file copy progress (with Cancel) and a notification when done";
            showTransferProgress.Margin = new Padding(em * 2, 3, 3, 3);
            startWithWindows.Text = "Start with Windows";
            useService.Text = "Service mode: control the lock screen and UAC prompts (one-time admin install)";
            shareClipboard.CheckedChanged += (s, e) => transferFile.Enabled = shareClipboard.Checked;

            foreach (var cb in new[] { shareClipboard, transferFile, showTransferProgress, startWithWindows, useService })
            {
                cb.AutoSize = true;
                grid.Controls.Add(cb);
            }

            return NewGroup("Options", grid);
        }

        private GroupBox BuildAdvancedGroup()
        {
            var outer = NewGrid(1);
            var grid = NewGrid(2);

            grid.Controls.Add(NewLabel("Security key:"));
            grid.Controls.Add(BuildKeyRow());
            grid.Controls.Add(new Label());
            grid.Controls.Add(NewHint("Shared by all paired PCs and set by pairing; you don't need to type it. At least 16 characters, identical on every PC."));

            tcpPort.Minimum = 1024;
            tcpPort.Maximum = 65534;
            tcpPort.Width = em * 6;
            grid.Controls.Add(NewLabel("TCP port:"));
            var portRow = NewFlow();
            portRow.Controls.Add(tcpPort);
            portRow.Controls.Add(NewHint("uses this port and the next; same on every PC"));
            grid.Controls.Add(portRow);

            easyMouse.DropDownStyle = ComboBoxStyle.DropDownList;
            easyMouse.Width = em * 18;
            easyMouse.Items.AddRange(EasyMouseChoices.Select(c => (object)c.Label).ToArray());
            grid.Controls.Add(NewLabel("Easy Mouse:"));
            grid.Controls.Add(easyMouse);

            switchMachine.DropDownStyle = ComboBoxStyle.DropDownList;
            switchMachine.Width = em * 18;
            switchMachine.Items.AddRange(SwitchChoices.Select(c => (object)c.Label).ToArray());
            grid.Controls.Add(NewLabel("Switch to PC:"));
            grid.Controls.Add(switchMachine);

            foreach (var (label, box) in new[]
            {
                ("Toggle Easy Mouse:", hkToggleEasyMouse),
                ("Lock this PC:", hkLockMachine),
                ("Reconnect:", hkReconnect),
                ("Control all PCs:", hkSwitchToAll),
            })
            {
                box.Width = em * 18;
                box.PlaceholderText = "e.g. Win+Ctrl+Alt+E (empty = off)";
                grid.Controls.Add(NewLabel(label));
                grid.Controls.Add(box);
            }

            name2Ip.Multiline = true;
            name2Ip.ScrollBars = ScrollBars.Vertical;
            name2Ip.Width = em * 18;
            name2Ip.Height = Font.Height * 4;
            name2Ip.PlaceholderText = "LAPTOP1 192.168.1.21";
            grid.Controls.Add(NewLabel("Name → IP:"));
            grid.Controls.Add(name2Ip);
            outer.Controls.Add(grid);

            var checks = NewGrid(2);
            foreach (var (cb, text) in new[]
            {
                (sameSubnetOnly, "Only connect within my subnet"),
                (validateRemoteIp, "Validate remote IP (reverse DNS)"),
                (hideMouseAtEdge, "Hide the cursor at the screen edge"),
                (drawMouseCursor, "Draw the cursor on other PCs"),
                (blockScreenSaver, "Keep other PCs' screen savers off"),
                (moveMouseRelatively, "Move the mouse relatively"),
                (blockMouseAtCorners, "Block switching at screen corners"),
                (showStatusMessages, "Show clipboard/network messages"),
                (noEasyMouseFullscreen, "No Easy Mouse over full-screen apps"),
                (allowInsecure, "Allow older versions (not secure)"),
            })
            {
                cb.Text = text;
                cb.AutoSize = true;
                checks.Controls.Add(cb);
            }

            outer.Controls.Add(checks);

            advancedGroup.Text = "Advanced";
            advancedGroup.AutoSize = true;
            advancedGroup.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            advancedGroup.Dock = DockStyle.Fill;
            advancedGroup.Padding = new Padding(em / 2);
            outer.Dock = DockStyle.Fill;
            advancedGroup.Controls.Add(outer);
            return advancedGroup;
        }

        private FlowLayoutPanel BuildButtons()
        {
            var row = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, em / 2, 0, 0),
            };

            var save = NewButton("Save && apply", (s, e) => SaveAndApply());
            var cancel = NewButton("Cancel", (s, e) => Close());
            var json = NewButton("Open JSON…", (s, e) => LiteTray.OpenUserConfigInEditor());
            var check = NewButton("Check connection…", (s, e) => DiagnosticsForm.ShowFor(this, loaded));
            AcceptButton = save;
            CancelButton = cancel;
            row.Controls.AddRange(new Control[] { save, cancel, json, check });
            return row;
        }

        private void SetAdvancedVisible(bool visible)
        {
            advancedGroup.Visible = visible;
            advancedToggle.Text = visible ? "Hide advanced settings" : "Show advanced settings";
        }

        private void AddThisPc()
        {
            string me = LiteConfig.NormalizeName(Environment.MachineName);
            if (layoutEditor.Slots.Contains(me, StringComparer.OrdinalIgnoreCase))
            {
                SetStatus(me + " is already in the layout.", ok: true);
            }
            else if (!layoutEditor.AddName(me))
            {
                SetStatus("All 4 slots are used.", ok: false);
            }
        }

        // ---- values -------------------------------------------------------------------------

        private void LoadValues(LiteConfig cfg)
        {
            keyBox.Text = cfg.SecurityKey;
            layoutEditor.Slots = cfg.MatrixSlots();
            oneRow.Checked = cfg.Layout.OneRow;
            twoRows.Checked = !cfg.Layout.OneRow;
            layoutEditor.OneRow = cfg.Layout.OneRow;
            wrapMouse.Checked = cfg.Layout.WrapMouse;

            shareClipboard.Checked = cfg.ShareClipboard;
            transferFile.Checked = cfg.TransferFile;
            showTransferProgress.Checked = cfg.ShowTransferProgress;
            transferFile.Enabled = cfg.ShareClipboard;
            startWithWindows.Checked = cfg.StartWithWindows;
            useService.Checked = cfg.UseService;

            var adv = cfg.Advanced;
            tcpPort.Value = Math.Clamp(adv.TcpPort, (int)tcpPort.Minimum, (int)tcpPort.Maximum);
            easyMouse.SelectedIndex = Math.Max(0, Array.FindIndex(EasyMouseChoices, c => c.Value == adv.EasyMouse));
            string sw = (adv.Hotkeys.SwitchMachine ?? string.Empty).Trim();
            switchMachine.SelectedIndex = Math.Max(0, Array.FindIndex(SwitchChoices, c => string.Equals(c.Value, sw, StringComparison.OrdinalIgnoreCase)));
            hkToggleEasyMouse.Text = adv.Hotkeys.ToggleEasyMouse;
            hkLockMachine.Text = adv.Hotkeys.LockMachine;
            hkReconnect.Text = adv.Hotkeys.Reconnect;
            hkSwitchToAll.Text = adv.Hotkeys.SwitchToAllPcs;
            name2Ip.Text = string.Join("\r\n", adv.Name2Ip);

            sameSubnetOnly.Checked = adv.SameSubnetOnly;
            validateRemoteIp.Checked = adv.ValidateRemoteIp;
            hideMouseAtEdge.Checked = adv.HideMouseAtScreenEdge;
            drawMouseCursor.Checked = adv.DrawMouseCursor;
            blockScreenSaver.Checked = adv.BlockScreenSaverOnOtherMachines;
            moveMouseRelatively.Checked = adv.MoveMouseRelatively;
            blockMouseAtCorners.Checked = adv.BlockMouseAtScreenCorners;
            showStatusMessages.Checked = adv.ShowClipboardAndNetworkStatusMessages;
            noEasyMouseFullscreen.Checked = adv.DisableEasyMouseWhenForegroundWindowIsFullscreen;
            allowInsecure.Checked = adv.AllowInsecureConnections;
        }

        /// <summary>Builds the config to save: the loaded config (so fields not on the form survive) with the form's values.</summary>
        private void RunPairing(bool host)
        {
            if (HasUnsavedChanges())
            {
                SetStatus("Save your changes first (Save & apply), then pair.", ok: false);
                return;
            }

            using var dialog = new Pairing.PairingForm(host, settingsPath);
            _ = dialog.ShowDialog(this);
            if (dialog.SavedConfig == null)
            {
                return;
            }

            // The pairing already saved settings.json; show and apply what it saved.
            loaded = dialog.SavedConfig;
            LoadValues(loaded);
            baseline = null;
            _ = HasUnsavedChanges();
            SetStatus("Paired. Settings saved and applied.", ok: true);
            afterSave?.Invoke(loaded);
        }

        // What the form showed right after loading, so differences in formatting (e.g. "f1" vs "F1") are not "changes".
        private string baseline;

        private bool HasUnsavedChanges()
        {
            static string Canonical(LiteConfig c)
            {
                var copy = c.Clone();
                var slots = copy.MatrixSlots().ToList();
                while (slots.Count > 0 && slots[^1].Length == 0)
                {
                    slots.RemoveAt(slots.Count - 1);
                }

                copy.Machines = slots;
                return System.Text.Json.JsonSerializer.Serialize(copy, LiteConfig.JsonOptions);
            }

            return Canonical(ReadValues()) != (baseline ??= Canonical(ReadValues()));
        }

        private LiteConfig ReadValues()
        {
            var cfg = loaded.Clone();
            cfg.SecurityKey = keyBox.Text;

            var names = layoutEditor.Slots.ToList();
            while (names.Count > 0 && string.IsNullOrWhiteSpace(names[^1]))
            {
                names.RemoveAt(names.Count - 1);
            }

            cfg.Machines = names;
            cfg.Layout.OneRow = oneRow.Checked;
            cfg.Layout.WrapMouse = wrapMouse.Checked;
            cfg.ShareClipboard = shareClipboard.Checked;
            cfg.TransferFile = transferFile.Checked;
            cfg.ShowTransferProgress = showTransferProgress.Checked;
            cfg.StartWithWindows = startWithWindows.Checked;
            cfg.UseService = useService.Checked;

            var adv = cfg.Advanced;
            adv.TcpPort = (int)tcpPort.Value;
            adv.EasyMouse = EasyMouseChoices[Math.Max(0, easyMouse.SelectedIndex)].Value;
            adv.Hotkeys.SwitchMachine = SwitchChoices[Math.Max(0, switchMachine.SelectedIndex)].Value;
            adv.Hotkeys.ToggleEasyMouse = hkToggleEasyMouse.Text.Trim();
            adv.Hotkeys.LockMachine = hkLockMachine.Text.Trim();
            adv.Hotkeys.Reconnect = hkReconnect.Text.Trim();
            adv.Hotkeys.SwitchToAllPcs = hkSwitchToAll.Text.Trim();
            adv.Name2Ip = name2Ip.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

            adv.SameSubnetOnly = sameSubnetOnly.Checked;
            adv.ValidateRemoteIp = validateRemoteIp.Checked;
            adv.HideMouseAtScreenEdge = hideMouseAtEdge.Checked;
            adv.DrawMouseCursor = drawMouseCursor.Checked;
            adv.BlockScreenSaverOnOtherMachines = blockScreenSaver.Checked;
            adv.MoveMouseRelatively = moveMouseRelatively.Checked;
            adv.BlockMouseAtScreenCorners = blockMouseAtCorners.Checked;
            adv.ShowClipboardAndNetworkStatusMessages = showStatusMessages.Checked;
            adv.DisableEasyMouseWhenForegroundWindowIsFullscreen = noEasyMouseFullscreen.Checked;
            adv.AllowInsecureConnections = allowInsecure.Checked;

            cfg.Normalize();
            return cfg;
        }

        private void SaveAndApply()
        {
            var cfg = ReadValues();
            var errors = cfg.Validate(out _);
            if (errors.Count > 0)
            {
                SetStatus(string.Join("\r\n", errors.Select(Friendly)), ok: false);
                if (errors.Any(e => e.StartsWith("advanced.", StringComparison.Ordinal) || e.StartsWith("securityKey", StringComparison.Ordinal)))
                {
                    SetAdvancedVisible(true);
                }

                return;
            }

            if (!commentsAcknowledged && File.Exists(settingsPath) && LiteConfig.HasComments(settingsPath))
            {
                if (MessageBox.Show(this, "settings.json contains comments, which will be removed. The current file is kept as settings.json.bak.", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                {
                    return;
                }

                commentsAcknowledged = true;
            }

            try
            {
                cfg.Save(settingsPath);
                LiteLog.Write(nameof(LiteSettingsForm), "Saved " + settingsPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                SetStatus("Could not save: " + e.Message, ok: false);
                return;
            }

            Close();
            afterSave?.Invoke(cfg);
        }

        // Maps validation messages (which name JSON keys) to the labels on this form.
        private static string Friendly(string error)
        {
            int colon = error.IndexOf(": ", StringComparison.Ordinal);
            string key = colon > 0 ? error[..colon] : string.Empty;
            string label = key switch
            {
                "securityKey" => "Security key",
                "machines" => "Machines",
                "advanced.name2Ip" => "Name → IP",
                "advanced.hotkeys.toggleEasyMouse" => "Toggle Easy Mouse",
                "advanced.hotkeys.lockMachine" => "Lock this PC",
                "advanced.hotkeys.reconnect" => "Reconnect",
                "advanced.hotkeys.switchToAllPcs" => "Control all PCs",
                _ => null,
            };

            if (key == "securityKey")
            {
                // Upstream's message also says the key "must be auto generated", which is not required here.
                return "Security key (advanced settings): use at least 16 characters (spaces are ignored), or pair the PCs again.";
            }

            return label == null ? error : label + ": " + error[(colon + 2)..];
        }

        private void SetStatus(string text, bool ok)
        {
            status.ForeColor = ok ? SystemColors.ControlText : Color.Firebrick;
            status.Text = text;
            status.Visible = text.Length > 0;
        }

        // ---- small builders ---------------------------------------------------------------

        private GroupBox NewGroup(string title, Control content)
        {
            content.Dock = DockStyle.Fill;
            var group = new GroupBox
            {
                Text = title,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Padding = new Padding(em / 2),
                Margin = new Padding(3, 3, 3, em / 2),
            };
            group.Controls.Add(content);
            return group;
        }

        private static TableLayoutPanel NewGrid(int columns) => new()
        {
            ColumnCount = columns,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };

        private static FlowLayoutPanel NewFlow() => new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = Padding.Empty,
        };

        private static Label NewLabel(string text) => new()
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 6, 3, 3),
        };

        private Label NewHint(string text) => new()
        {
            Text = text,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 6, 3, 3),
            MaximumSize = new Size(em * 34, 0),
        };

        private static Button NewButton(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(80, 0) };
            b.Click += onClick;
            return b;
        }
    }
}
