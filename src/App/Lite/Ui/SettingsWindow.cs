// MwbLite Settings (WPF, Fluent theme): edits %LOCALAPPDATA%\MwbLite\settings.json, laid out like Windows 11
// Settings: a sidebar of pages (PCs, Pairing, Clipboard & files, Connection, Advanced), cards and switches.
//
// It edits a copy of the loaded config (values not on the window are kept), validates with the same LiteConfig.Validate
// as "Reload config", and writes the file only on Save. It always runs as the signed-in user, in its own process
// ("MwbLite.exe --settings"); the running MwbLite picks the saved file up (see LiteTray).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

using WpfButton = System.Windows.Controls.Button;
using WpfClipboard = System.Windows.Clipboard;
using WpfMessageBox = System.Windows.MessageBox;

namespace MouseWithoutBorders.Lite.Ui
{
    internal sealed partial class SettingsWindow : Window
    {
        private static readonly (string Label, EasyMouseOption Value)[] EasyMouseChoices =
        {
            ("Cross screen edges freely", EasyMouseOption.Enable),
            ("Hold Ctrl to cross an edge", EasyMouseOption.Ctrl),
            ("Hold Shift to cross an edge", EasyMouseOption.Shift),
            ("Off: switch with hotkeys only", EasyMouseOption.Disable),
        };

        private static readonly (string Label, string Value)[] SwitchChoices =
        {
            ("Ctrl+Alt+F1 … F4", "F1"),
            ("Ctrl+Alt+1 … 4", "1"),
            ("Off", "disabled"),
        };

        private static readonly (string Icon, string Title)[] Pages =
        {
            ("", "PCs"),
            ("", "Pairing"),
            ("", "Clipboard & files"),
            ("", "Connection"),
            ("", "Advanced"),
            ("", "About"),
        };

        private readonly string settingsPath;
        private readonly Action<LiteConfig> afterSave;
        private readonly LayoutModel layout = new();
        private readonly LayoutEditorView layoutView;
        private readonly ContentControl page = new();
        private readonly ListBox nav = new();
        private readonly Dictionary<string, FrameworkElement> pages = new();
        private readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };

        // PCs
        private readonly RadioButton oneRow = new() { Content = "One row", Margin = new Thickness(0, 0, 16, 0), GroupName = "rows" };
        private readonly RadioButton twoRows = new() { Content = "Two rows (2 × 2)", GroupName = "rows" };
        private readonly CheckBox wrapMouse = Kit.Switch();
        private readonly StackPanel pcList = new();

        // Clipboard & files
        private readonly CheckBox shareClipboard = Kit.Switch();
        private readonly CheckBox transferFile = Kit.Switch();
        private readonly CheckBox showTransferProgress = Kit.Switch();

        // About
        private readonly CheckBox checkForUpdates = Kit.Switch();
        private readonly TextBlock updateStatus = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock updateDetail = Kit.Hint(string.Empty);
        private readonly TextBox releaseNotes = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
        private readonly ProgressBar updateProgress = new() { Height = 4, Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
        private WpfButton checkUpdateButton;
        private WpfButton updateButton;
        private ReleaseInfo latestRelease;

        // Connection
        private readonly CheckBox startWithWindows = Kit.Switch();
        private readonly CheckBox useService = Kit.Switch();
        private readonly StackPanel checkResults = new();
        private readonly TextBlock checkSummary = Kit.Hint(string.Empty);
        private readonly WpfButton checkButton;
        private readonly WpfButton fixFirewall;
        private CancellationTokenSource checkCts;

        // Advanced
        private readonly PasswordBox keyHidden = new() { Width = 220, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
        private readonly TextBox keyShown = new() { Width = 220, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Visibility = Visibility.Collapsed };
        private readonly CheckBox showKey = new() { Content = "Show", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBox tcpPort = new() { Width = 90 };
        private readonly ComboBox easyMouse = new() { Width = 240 };
        private readonly ComboBox switchMachine = new() { Width = 240 };
        private readonly TextBox hkToggleEasyMouse = new() { Width = 240 };
        private readonly TextBox hkLockMachine = new() { Width = 240 };
        private readonly TextBox hkReconnect = new() { Width = 240 };
        private readonly TextBox hkSwitchToAll = new() { Width = 240 };
        private readonly TextBox name2Ip = new() { Width = 240, Height = 84, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        private readonly CheckBox sameSubnetOnly = Kit.Switch();
        private readonly CheckBox validateRemoteIp = Kit.Switch();
        private readonly CheckBox hideMouseAtEdge = Kit.Switch();
        private readonly CheckBox drawMouseCursor = Kit.Switch();
        private readonly CheckBox blockScreenSaver = Kit.Switch();
        private readonly CheckBox moveMouseRelatively = Kit.Switch();
        private readonly CheckBox blockMouseAtCorners = Kit.Switch();
        private readonly CheckBox showStatusMessages = Kit.Switch();
        private readonly CheckBox noEasyMouseFullscreen = Kit.Switch();
        private readonly CheckBox allowInsecure = Kit.Switch();

        // Footer
        private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };

        private LiteConfig loaded;
        private string baseline;
        private bool commentsAcknowledged;
        private bool closingAfterSave;

        private SettingsWindow(string settingsPath, LiteConfig config, Action<LiteConfig> afterSave)
        {
            this.settingsPath = settingsPath;
            this.afterSave = afterSave;
            loaded = config;

            Title = "MwbLite Settings";
            Width = 980;
            Height = 720;
            MinWidth = 760;
            MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            try
            {
                Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(LitePaths.ExePath));
            }
            catch (Exception)
            {
                // Keep the default icon.
            }

            layout.ThisPc = ThisPc;
            layoutView = new LayoutEditorView(layout) { Margin = new Thickness(0, 6, 0, 6) };
            layoutView.StateOf = name => LastStatus?.Pcs.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.State;
            checkButton = Kit.Button("Check now", (_, _) => StartCheck(), accent: true);
            fixFirewall = Kit.Button("Fix firewall…", (_, _) => FixFirewall());
            fixFirewall.Visibility = Visibility.Collapsed;

            BuildPages();
            Content = BuildShell();
            LoadValues(config);
            baseline = Canonical(ReadValues());

            statusTimer.Tick += (_, _) => RefreshStatus();
            Loaded += (_, _) =>
            {
                RefreshStatus();
                statusTimer.Start();
            };
            Closing += OnClosing;
        }

        private StatusSnapshot LastStatus { get; set; }

        /// <summary>This PC's name as shown (the README screenshots use a neutral one).</summary>
        internal static string ThisPc { get; set; } = LiteConfig.NormalizeName(Environment.MachineName);

        /// <summary>Where live status comes from: the running MwbLite's status.json (previews substitute a sample).</summary>
        internal static Func<StatusSnapshot> StatusSource { get; set; } = () => LiteStatus.ReadLatest(TimeSpan.FromSeconds(20));

        /// <summary>Preview only: shows these check results instead of running the checks.</summary>
        internal void ShowCheckResults(System.Collections.Generic.IEnumerable<CheckItem> results)
        {
            checkResults.Children.Clear();
            var items = new List<CheckItem>();
            foreach (CheckItem item in results)
            {
                AddResult(item, items);
            }

            FinishCheck(items);
        }

        internal int PageCount => Pages.Length;

        internal string PageName(int index) => Pages[index].Title;

        internal void SelectPage(int index) => nav.SelectedIndex = index;

        /// <summary>Opens a page by name ("about", "pcs", …); unknown or null names leave the first page.</summary>
        internal void SelectPage(string name)
        {
            int i = Array.FindIndex(Pages, p => p.Title.Split(' ')[0].Equals(name ?? string.Empty, StringComparison.OrdinalIgnoreCase));
            if (i >= 0)
            {
                nav.SelectedIndex = i;
            }
        }

        internal void RunCheckForPreview() => StartCheck();

        /// <summary>Loads settings.json (creating it if missing) and returns the window, or null if the user cancelled.</summary>
        internal static SettingsWindow Create(string settingsPath, Action<LiteConfig> afterSave)
        {
            try
            {
                _ = LiteConfig.WriteDefaultIfMissing(settingsPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LiteLog.Write(nameof(SettingsWindow), "Cannot create " + settingsPath + ": " + e.Message);
            }

            var cfg = LiteConfig.TryParse(settingsPath, out string error);
            if (cfg == null)
            {
                MessageBoxResult answer = WpfMessageBox.Show(
                    error + "\n\nYes: open settings.json in Notepad to fix it.\nNo: start from default settings (your file is kept as settings.json.bak when you save).",
                    "MwbLite Settings",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning);
                if (answer == MessageBoxResult.Yes)
                {
                    LiteTray.OpenUserConfigInEditor();
                    return null;
                }

                if (answer != MessageBoxResult.No)
                {
                    return null;
                }

                cfg = LiteConfig.CreateDefault();
            }

            return new SettingsWindow(settingsPath, cfg, afterSave);
        }

        // ---- shell: sidebar, page, footer -----------------------------------------------------------------

        private UIElement BuildShell()
        {
            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Sidebar
            var side = new DockPanel { Margin = new Thickness(12, 16, 8, 12) };
            var brand = Kit.Horizontal(Kit.Logo(28), new TextBlock { Text = "MwbLite", FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
            brand.Margin = new Thickness(8, 0, 0, 6);
            DockPanel.SetDock(brand, Dock.Top);
            side.Children.Add(brand);
            var thisPc = Kit.Hint("This PC: " + ThisPc);
            thisPc.Margin = new Thickness(8, 0, 0, 16);
            DockPanel.SetDock(thisPc, Dock.Top);
            side.Children.Add(thisPc);

            foreach (var (icon, title) in Pages)
            {
                var item = Kit.Horizontal(Kit.Icon(icon, 16), new TextBlock { Text = title, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                item.Margin = new Thickness(4, 6, 4, 6);
                nav.Items.Add(new ListBoxItem { Content = item, Tag = title, Style = Kit.NavItemStyle.Value });
            }

            nav.BorderThickness = new Thickness(0);
            nav.Background = System.Windows.Media.Brushes.Transparent;
            nav.SelectionChanged += (_, _) => page.Content = pages[(string)((ListBoxItem)nav.SelectedItem).Tag];
            side.Children.Add(nav);
            root.Children.Add(side);

            // Page
            var scroller = new ScrollViewer
            {
                Content = page,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(28, 24, 32, 24),
            };
            Grid.SetColumn(scroller, 1);
            root.Children.Add(scroller);

            // Footer
            var footer = new DockPanel { Margin = new Thickness(28, 10, 32, 16), LastChildFill = true };
            var buttons = Kit.Horizontal(Kit.Button("Save", (_, _) => SaveAndApply(), accent: true), Kit.Button("Cancel", (_, _) => Close()));
            DockPanel.SetDock(buttons, Dock.Right);
            footer.Children.Add(buttons);
            footer.Children.Add(message);
            Grid.SetColumn(footer, 1);
            Grid.SetRow(footer, 1);
            root.Children.Add(footer);

            nav.SelectedIndex = 0;
            return root;
        }

        private void BuildPages()
        {
            // PCs
            oneRow.Checked += (_, _) => layout.OneRow = true;
            twoRows.Checked += (_, _) => layout.OneRow = false;
            var addThis = Kit.Button("Add this PC", (_, _) => AddThisPc());
            pages["PCs"] = Kit.Stack(
                Kit.PageTitle("PCs"),
                Kit.Card(Kit.Stack(
                    Kit.Horizontal(oneRow, twoRows),
                    layoutView,
                    Kit.Hint("Drag the PCs to match where they sit. The cursor crosses to a PC by leaving the screen on that side. Double-click to rename or add, Delete to remove. Other PCs follow when you save."))),
                Kit.Row("", "Wrap the cursor around the outer edges", "Leaving the last PC brings the cursor back to the first.", wrapMouse),
                Kit.Row("", "Add this PC to the layout", "Puts " + ThisPc + " in the first empty slot.", addThis),
                Kit.Section("Status"),
                Kit.Card(pcList));

            // Pairing
            pages["Pairing"] = Kit.Stack(
                Kit.PageTitle("Pairing"),
                Kit.Row("", "Add a PC", "Press this here, then Join on the new PC. Both show a code to compare.", Kit.Button("Add a PC…", (_, _) => RunPairing(host: true), accent: true)),
                Kit.Row("", "Join a PC", "Press this on a new PC while the other one is in Add a PC.", Kit.Button("Join…", (_, _) => RunPairing(host: false))),
                Kit.Hint("Pairing gives the new PC this group's security key and layout. Nothing is sent until you confirm the same 6-digit code on both PCs."));

            // Clipboard & files
            shareClipboard.Checked += (_, _) => transferFile.IsEnabled = true;
            shareClipboard.Unchecked += (_, _) => transferFile.IsEnabled = false;
            pages["Clipboard & files"] = Kit.Stack(
                Kit.PageTitle("Clipboard & files"),
                Kit.Row("", "Share the clipboard", "Copy text and images on one PC, paste on another.", shareClipboard),
                Kit.Row("", "Transfer files and folders", "Copy and paste up to 100 MB in total, or drag and drop any size.", transferFile),
                Kit.Row("", "Show file copy progress", "A small window with progress and Cancel, and a notification when a copy finishes.", showTransferProgress));

            // Connection
            pages["Connection"] = Kit.Stack(
                Kit.PageTitle("Connection"),
                Kit.Row("", "Start with Windows", "Start MwbLite when you sign in.", startWithWindows),
                Kit.Row("", "Control the lock screen and UAC prompts", "Service mode. Needs a one-time admin install; takes effect after MwbLite restarts.", useService),
                Kit.Section("Check connection"),
                Kit.Card(Kit.Stack(
                    Kit.Hint("Tests this PC's settings, firewall and service, then tries a secure connection to each PC with your security key."),
                    WithTopMargin(Kit.Horizontal(checkButton, fixFirewall, Kit.Button("Copy results", (_, _) => CopyResults())), 10),
                    WithTopMargin(checkSummary, 10),
                    checkResults)));

            // Advanced
            keyHidden.PasswordChanged += (_, _) =>
            {
                if (keyShown.Text != keyHidden.Password)
                {
                    keyShown.Text = keyHidden.Password;
                }
            };
            keyShown.TextChanged += (_, _) =>
            {
                if (keyHidden.Password != keyShown.Text)
                {
                    keyHidden.Password = keyShown.Text;
                }
            };
            showKey.Checked += (_, _) => SetKeyVisible(true);
            showKey.Unchecked += (_, _) => SetKeyVisible(false);
            var keyRow = Kit.Horizontal(keyHidden, keyShown, showKey, Kit.Button("Copy", (_, _) => CopyKey()), Kit.Button("Reset pairing…", (_, _) => ResetPairing()));

            easyMouse.ItemsSource = EasyMouseChoices.Select(c => c.Label).ToList();
            switchMachine.ItemsSource = SwitchChoices.Select(c => c.Label).ToList();
            foreach (TextBox hk in new[] { hkToggleEasyMouse, hkLockMachine, hkReconnect, hkSwitchToAll })
            {
                hk.ToolTip = "e.g. Win+Ctrl+Alt+E (empty = off)";
            }

            pages["Advanced"] = Kit.Stack(
                Kit.PageTitle("Advanced"),
                Kit.Row("", "Security key", "Shared by all paired PCs and set by pairing; you don't need to type it. Reset pairing makes a new one, which removes this PC from the group until it's paired again.", keyRow, below: true),
                Kit.Row("", "TCP port", "MwbLite uses this port and the next. Must be the same on every PC; takes effect after a restart.", tcpPort),
                Kit.Section("Mouse and keyboard"),
                Kit.Row("", "Easy Mouse", "How the cursor crosses to another PC.", easyMouse),
                Kit.Row("", "Switch to a PC", "Hotkeys for slots 1 to 4.", switchMachine),
                Kit.Row(null, "Toggle Easy Mouse", null, hkToggleEasyMouse),
                Kit.Row(null, "Lock this PC", null, hkLockMachine),
                Kit.Row(null, "Reconnect", null, hkReconnect),
                Kit.Row(null, "Control all PCs", null, hkSwitchToAll),
                Kit.Row(null, "Hide the cursor at the screen edge", null, hideMouseAtEdge),
                Kit.Row(null, "Draw the cursor on other PCs", null, drawMouseCursor),
                Kit.Row(null, "Move the mouse relatively", null, moveMouseRelatively),
                Kit.Row(null, "Block switching at screen corners", null, blockMouseAtCorners),
                Kit.Row(null, "No Easy Mouse over full-screen apps", null, noEasyMouseFullscreen),
                Kit.Row(null, "Keep other PCs' screen savers off", null, blockScreenSaver),
                Kit.Section("Network"),
                Kit.Row("", "Name → IP", "One per line, e.g. LAPTOP1 192.168.1.21. For PCs whose names don't resolve.", name2Ip, below: true),
                Kit.Row(null, "Only connect within my subnet", null, sameSubnetOnly),
                Kit.Row(null, "Validate remote IP (reverse DNS)", null, validateRemoteIp),
                Kit.Row(null, "Show clipboard and network messages", "Upstream's detailed notifications.", showStatusMessages),
                Kit.Row("", "Allow older versions (not secure)", "Lets PCs without the secure connection (MwbLite before 0.6, PowerToys) connect. Only for a short changeover.", allowInsecure),
                Kit.Section("Settings file"),
                Kit.Row("", "settings.json", settingsPath, Kit.Button("Open in Notepad", (_, _) => LiteTray.OpenUserConfigInEditor())));

            pages["About"] = BuildAbout();
        }

        private static T WithTopMargin<T>(T element, double top)
            where T : FrameworkElement
        {
            element.Margin = new Thickness(0, top, 0, 0);
            return element;
        }

        private void SetKeyVisible(bool visible)
        {
            keyShown.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            keyHidden.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        }

        private string KeyText => keyShown.Text;

        // ---- live status ----------------------------------------------------------------------------------

        private void RefreshStatus()
        {
            LastStatus = StatusSource();
            pcList.Children.Clear();
            if (LastStatus == null)
            {
                pcList.Children.Add(Kit.Hint("MwbLite isn't running on this PC, so there's no live status. Start it from the Start menu."));
            }
            else if (LastStatus.OtherCount == 0)
            {
                pcList.Children.Add(Kit.Hint("No other PCs yet. Pair one on the Pairing page."));
            }
            else
            {
                foreach (PcStatus pc in LastStatus.Pcs)
                {
                    var name = new TextBlock { Text = pc.Name, Width = 180, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                    var state = Kit.Text(LiteStatus.StateText(pc.State) + (pc.Address != null ? "   " + pc.Address : string.Empty));
                    var line = Kit.Horizontal(Kit.Dot(Kit.DotBrushKey(pc.State)), name, state);
                    line.Margin = new Thickness(0, 4, 0, 4);
                    pcList.Children.Add(line);
                }
            }

            layoutView.Rebuild();
        }

        // ---- values ---------------------------------------------------------------------------------------

        private void LoadValues(LiteConfig cfg)
        {
            keyShown.Text = cfg.SecurityKey;
            layout.Slots = cfg.MatrixSlots();
            layout.OneRow = cfg.Layout.OneRow;
            oneRow.IsChecked = cfg.Layout.OneRow;
            twoRows.IsChecked = !cfg.Layout.OneRow;
            wrapMouse.IsChecked = cfg.Layout.WrapMouse;

            shareClipboard.IsChecked = cfg.ShareClipboard;
            transferFile.IsChecked = cfg.TransferFile;
            transferFile.IsEnabled = cfg.ShareClipboard;
            showTransferProgress.IsChecked = cfg.ShowTransferProgress;
            checkForUpdates.IsChecked = cfg.CheckForUpdates;
            startWithWindows.IsChecked = cfg.StartWithWindows;
            useService.IsChecked = cfg.UseService;

            var adv = cfg.Advanced;
            tcpPort.Text = adv.TcpPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            easyMouse.SelectedIndex = Math.Max(0, Array.FindIndex(EasyMouseChoices, c => c.Value == adv.EasyMouse));
            string sw = (adv.Hotkeys.SwitchMachine ?? string.Empty).Trim();
            switchMachine.SelectedIndex = Math.Max(0, Array.FindIndex(SwitchChoices, c => string.Equals(c.Value, sw, StringComparison.OrdinalIgnoreCase)));
            hkToggleEasyMouse.Text = adv.Hotkeys.ToggleEasyMouse;
            hkLockMachine.Text = adv.Hotkeys.LockMachine;
            hkReconnect.Text = adv.Hotkeys.Reconnect;
            hkSwitchToAll.Text = adv.Hotkeys.SwitchToAllPcs;
            name2Ip.Text = string.Join("\r\n", adv.Name2Ip);

            sameSubnetOnly.IsChecked = adv.SameSubnetOnly;
            validateRemoteIp.IsChecked = adv.ValidateRemoteIp;
            hideMouseAtEdge.IsChecked = adv.HideMouseAtScreenEdge;
            drawMouseCursor.IsChecked = adv.DrawMouseCursor;
            blockScreenSaver.IsChecked = adv.BlockScreenSaverOnOtherMachines;
            moveMouseRelatively.IsChecked = adv.MoveMouseRelatively;
            blockMouseAtCorners.IsChecked = adv.BlockMouseAtScreenCorners;
            showStatusMessages.IsChecked = adv.ShowClipboardAndNetworkStatusMessages;
            noEasyMouseFullscreen.IsChecked = adv.DisableEasyMouseWhenForegroundWindowIsFullscreen;
            allowInsecure.IsChecked = adv.AllowInsecureConnections;
        }

        /// <summary>The config to save: the loaded config (so fields not on the window survive) with the window's values.</summary>
        private LiteConfig ReadValues()
        {
            var cfg = loaded.Clone();
            cfg.SecurityKey = KeyText;

            var names = layout.Slots.ToList();
            while (names.Count > 0 && string.IsNullOrWhiteSpace(names[^1]))
            {
                names.RemoveAt(names.Count - 1);
            }

            cfg.Machines = names;
            cfg.Layout.OneRow = oneRow.IsChecked == true;
            cfg.Layout.WrapMouse = wrapMouse.IsChecked == true;
            cfg.ShareClipboard = shareClipboard.IsChecked == true;
            cfg.TransferFile = transferFile.IsChecked == true;
            cfg.ShowTransferProgress = showTransferProgress.IsChecked == true;
            cfg.CheckForUpdates = checkForUpdates.IsChecked == true;
            cfg.StartWithWindows = startWithWindows.IsChecked == true;
            cfg.UseService = useService.IsChecked == true;

            var adv = cfg.Advanced;
            adv.TcpPort = int.TryParse(tcpPort.Text.Trim(), out int port) ? port : -1;
            adv.EasyMouse = EasyMouseChoices[Math.Max(0, easyMouse.SelectedIndex)].Value;
            adv.Hotkeys.SwitchMachine = SwitchChoices[Math.Max(0, switchMachine.SelectedIndex)].Value;
            adv.Hotkeys.ToggleEasyMouse = hkToggleEasyMouse.Text.Trim();
            adv.Hotkeys.LockMachine = hkLockMachine.Text.Trim();
            adv.Hotkeys.Reconnect = hkReconnect.Text.Trim();
            adv.Hotkeys.SwitchToAllPcs = hkSwitchToAll.Text.Trim();
            adv.Name2Ip = name2Ip.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

            adv.SameSubnetOnly = sameSubnetOnly.IsChecked == true;
            adv.ValidateRemoteIp = validateRemoteIp.IsChecked == true;
            adv.HideMouseAtScreenEdge = hideMouseAtEdge.IsChecked == true;
            adv.DrawMouseCursor = drawMouseCursor.IsChecked == true;
            adv.BlockScreenSaverOnOtherMachines = blockScreenSaver.IsChecked == true;
            adv.MoveMouseRelatively = moveMouseRelatively.IsChecked == true;
            adv.BlockMouseAtScreenCorners = blockMouseAtCorners.IsChecked == true;
            adv.ShowClipboardAndNetworkStatusMessages = showStatusMessages.IsChecked == true;
            adv.DisableEasyMouseWhenForegroundWindowIsFullscreen = noEasyMouseFullscreen.IsChecked == true;
            adv.AllowInsecureConnections = allowInsecure.IsChecked == true;

            cfg.Normalize();
            return cfg;
        }

        // Compared so formatting differences (e.g. "f1" vs "F1", trailing empty slots) are not "changes".
        private static string Canonical(LiteConfig c)
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

        private bool HasUnsavedChanges() => Canonical(ReadValues()) != baseline;

        // ---- actions --------------------------------------------------------------------------------------

        private bool SaveAndApply()
        {
            var cfg = ReadValues();
            var errors = cfg.Validate(out _);
            if (errors.Count > 0)
            {
                SetMessage(string.Join("\n", errors.Select(Friendly)), ok: false);
                if (errors.Any(e => e.StartsWith("advanced.", StringComparison.Ordinal) || e.StartsWith("securityKey", StringComparison.Ordinal)))
                {
                    nav.SelectedIndex = Array.FindIndex(Pages, p => p.Title == "Advanced");
                }

                return false;
            }

            if (!commentsAcknowledged && File.Exists(settingsPath) && LiteConfig.HasComments(settingsPath))
            {
                if (WpfMessageBox.Show(this, "settings.json contains comments, which will be removed. The current file is kept as settings.json.bak.", Title, MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
                {
                    return false;
                }

                commentsAcknowledged = true;
            }

            try
            {
                cfg.Save(settingsPath);
                LiteLog.Write(nameof(SettingsWindow), "Saved " + settingsPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                SetMessage("Could not save: " + e.Message, ok: false);
                return false;
            }

            loaded = cfg;
            baseline = Canonical(cfg);
            closingAfterSave = true;
            Close();
            afterSave?.Invoke(cfg);
            return true;
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            statusTimer.Stop();
            checkCts?.Cancel();
            if (closingAfterSave || !HasUnsavedChanges())
            {
                return;
            }

            MessageBoxResult answer = WpfMessageBox.Show(this, "Save your changes?", Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel || (answer == MessageBoxResult.Yes && !SaveAndApply()))
            {
                e.Cancel = true;
                statusTimer.Start();
            }
        }

        private void AddThisPc()
        {
            string me = ThisPc;
            if (layout.Slots.Contains(me, StringComparer.OrdinalIgnoreCase))
            {
                SetMessage(me + " is already in the layout.", ok: true);
            }
            else if (!layout.AddName(me))
            {
                SetMessage("All 4 slots are used.", ok: false);
            }
        }

        private void RunPairing(bool host)
        {
            if (HasUnsavedChanges())
            {
                SetMessage("Save your changes first, then pair.", ok: false);
                return;
            }

            var dialog = new PairingWindow(host, settingsPath) { Owner = this };
            _ = dialog.ShowDialog();
            if (dialog.SavedConfig == null)
            {
                return;
            }

            // The pairing already saved settings.json; show and apply what it saved.
            loaded = dialog.SavedConfig;
            LoadValues(loaded);
            baseline = Canonical(ReadValues());
            SetMessage("Paired. Settings saved and applied.", ok: true);
            afterSave?.Invoke(loaded);
        }

        private void CopyKey()
        {
            if (KeyText.Length > 0)
            {
                WpfClipboard.SetText(KeyText);
                SetMessage("Key copied to the clipboard.", ok: true);
            }
        }

        private void ResetPairing()
        {
            if (KeyText.Length == 0 || WpfMessageBox.Show(
                this,
                "Create a new security key for this PC?\n\nAfter you save, this PC stops working with the others until you pair them again (Add a PC / Join). Use this to remove a PC from your group.",
                "Reset pairing",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) == MessageBoxResult.OK)
            {
                keyShown.Text = Encryption.CreateRandomKey();
                showKey.IsChecked = true;
            }
        }

        // ---- check connection -----------------------------------------------------------------------------

        private void StartCheck()
        {
            checkCts?.Cancel();
            checkCts = new CancellationTokenSource();
            CancellationToken ct = checkCts.Token;
            checkResults.Children.Clear();
            fixFirewall.Visibility = Visibility.Collapsed;
            checkButton.IsEnabled = false;
            checkSummary.Text = "Checking…";
            var items = new List<CheckItem>();
            LiteConfig cfg = loaded.Clone();

            _ = Task.Run(async () =>
            {
                try
                {
                    await LiteDiagnostics.RunAsync(cfg, item => Dispatcher.BeginInvoke(() => AddResult(item, items)), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception e)
                {
                    _ = Dispatcher.BeginInvoke(() => AddResult(new CheckItem(CheckResult.Warning, "Check", "Stopped: " + e.Message), items));
                }

                _ = Dispatcher.BeginInvoke(() => FinishCheck(items));
            });
        }

        private void AddResult(CheckItem item, List<CheckItem> items)
        {
            items.Add(item);
            string key = item.Result switch
            {
                CheckResult.Ok => "SystemFillColorSuccessBrush",
                CheckResult.Warning => "SystemFillColorCautionBrush",
                CheckResult.Problem => "SystemFillColorCriticalBrush",
                _ => "AccentFillColorDefaultBrush",
            };
            string glyph = item.Result switch
            {
                CheckResult.Ok => "",
                CheckResult.Warning => "",
                CheckResult.Problem => "",
                _ => "",
            };

            var icon = Kit.Icon(glyph, 14);
            icon.SetResourceReference(TextBlock.ForegroundProperty, key);
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 3, 12, 0);
            var title = new TextBlock { Text = item.Title, FontWeight = FontWeights.SemiBold };
            var text = Kit.Stack(title, Kit.Hint(item.Detail));
            var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(icon);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            checkResults.Children.Add(grid);
            if (item.FirewallFixable)
            {
                fixFirewall.Visibility = Visibility.Visible;
            }
        }

        private void FinishCheck(List<CheckItem> items)
        {
            checkButton.IsEnabled = true;
            int problems = items.Count(i => i.Result == CheckResult.Problem);
            int warnings = items.Count(i => i.Result == CheckResult.Warning);
            checkSummary.Text = problems == 0 && warnings == 0 ? "Everything checked out."
                : problems > 0 ? $"{problems} problem(s) found. Fix the first one, then check again."
                : $"{warnings} thing(s) to look at, nothing blocking.";
        }

        private void FixFirewall()
        {
            int port = int.TryParse(tcpPort.Text.Trim(), out int p) ? p : loaded.Advanced.TcpPort;
            int rc = LiteCommands.RepairFirewall(port);
            if (rc == 0)
            {
                StartCheck();
            }
            else if (rc != 1223)
            {
                _ = WpfMessageBox.Show(this, "Couldn't change the firewall (exit code " + rc + ").", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CopyResults()
        {
            var sb = new StringBuilder("MwbLite connection check, " + DateTime.Now.ToString("g", System.Globalization.CultureInfo.CurrentCulture) + ", on " + Environment.MachineName + "\r\n");
            foreach (Grid row in checkResults.Children.OfType<Grid>())
            {
                var text = (StackPanel)row.Children[1];
                sb.Append(((TextBlock)text.Children[0]).Text).Append(": ").Append(((TextBlock)text.Children[1]).Text).Append("\r\n");
            }

            WpfClipboard.SetText(sb.ToString());
            SetMessage("Results copied to the clipboard.", ok: true);
        }

        // ---- messages -------------------------------------------------------------------------------------

        // Maps validation messages (which name JSON keys) to the labels in this window.
        private static string Friendly(string error)
        {
            int colon = error.IndexOf(": ", StringComparison.Ordinal);
            string key = colon > 0 ? error[..colon] : string.Empty;
            if (key == "securityKey")
            {
                return error.Contains("another Windows account", StringComparison.Ordinal)
                    ? "Security key (Advanced): " + error[(colon + 2)..]
                    : "Security key (Advanced): use at least 16 characters (spaces are ignored), or pair the PCs again.";
            }

            string label = key switch
            {
                "machines" => "PCs",
                "advanced.tcpPort must be between 1024 and 65534 (MWB also uses tcpPort+1)." => "TCP port",
                "advanced.name2Ip" => "Name → IP",
                "advanced.hotkeys.toggleEasyMouse" => "Toggle Easy Mouse",
                "advanced.hotkeys.lockMachine" => "Lock this PC",
                "advanced.hotkeys.reconnect" => "Reconnect",
                "advanced.hotkeys.switchToAllPcs" => "Control all PCs",
                _ => null,
            };

            if (error.StartsWith("advanced.tcpPort", StringComparison.Ordinal))
            {
                return "TCP port (Advanced): use a number between 1024 and 65534.";
            }

            return label == null ? error : label + ": " + error[(colon + 2)..];
        }

        private void SetMessage(string text, bool ok)
        {
            message.Text = text;
            message.SetResourceReference(TextBlock.ForegroundProperty, ok ? "TextFillColorSecondaryBrush" : "SystemFillColorCriticalBrush");
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                _ = SaveAndApply();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        }
    }
}
