// MwbLite pairing window (WPF): "Add a PC" (host) or "Join" (joiner). The protocol is in Pairing/PairingSession
// and Pairing/PairingCrypto; this window shows progress, lists PCs waiting to be joined, and shows the 6-digit code
// to compare on both PCs.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using MouseWithoutBorders.Lite.Pairing;

using WpfButton = System.Windows.Controls.Button;

namespace MouseWithoutBorders.Lite.Ui
{
    internal sealed class PairingWindow : Window, IPairingUi
    {
        private readonly bool host;
        private readonly string settingsPath;
        private readonly CancellationTokenSource cts = new();

        private readonly TextBlock status = Kit.Text(string.Empty);
        private readonly ListBox hostsList = new() { Height = 140, Margin = new Thickness(0, 4, 0, 0) };
        private readonly TextBox manualHost = new() { Margin = new Thickness(0, 4, 0, 0) };
        private readonly StackPanel joinPanel = new();
        private readonly StackPanel codePanel = new() { Visibility = Visibility.Collapsed };
        private readonly TextBlock codePrompt = Kit.Text(string.Empty);
        private readonly TextBlock codeLabel = new() { FontSize = 44, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 10), FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Display, Segoe UI") };
        private readonly WpfButton primary;
        private readonly WpfButton cancel;
        private readonly List<DiscoveredHost> found = new();
        private readonly ObservableCollection<string> hostItems = new();

        private CancellationTokenSource discoveryCts;
        private TaskCompletionSource<bool> codeDecision;
        private bool finished;

        internal PairingWindow(bool host, string settingsPath)
        {
            this.host = host;
            this.settingsPath = settingsPath;
            Title = host ? "Add a PC" : "Join";
            Width = 480;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            primary = Kit.Button(host ? "Codes match" : "Join", (_, _) => OnPrimary(), accent: true);
            cancel = Kit.Button("Cancel", (_, _) => OnCancel());
            cancel.IsCancel = true;

            var heading = Kit.PageTitle(host ? "Add a PC" : "Join a PC");
            heading.FontSize = 22;
            heading.Margin = new Thickness(0, 0, 0, 10);

            hostsList.ItemsSource = hostItems;
            hostsList.SelectionChanged += (_, _) => UpdateJoinButton();
            hostsList.MouseDoubleClick += (_, _) => StartJoin();
            manualHost.TextChanged += (_, _) => UpdateJoinButton();
            joinPanel.Children.Add(Kit.Section("PCs waiting to be joined"));
            joinPanel.Children.Add(hostsList);
            joinPanel.Children.Add(Kit.Hint("Not listed? Type its computer name or IP address:"));
            joinPanel.Children.Add(manualHost);
            joinPanel.Visibility = host ? Visibility.Collapsed : Visibility.Visible;

            codePanel.Children.Add(codePrompt);
            codePanel.Children.Add(Kit.Card(codeLabel, new Thickness(0, 10, 0, 6)));
            codePanel.Children.Add(Kit.Hint("If the codes differ, press Cancel."));
            codeLabel.HorizontalAlignment = HorizontalAlignment.Center;

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            buttons.Children.Add(primary);
            buttons.Children.Add(cancel);

            Content = new Border
            {
                Padding = new Thickness(24),
                Child = Kit.Stack(heading, status, joinPanel, codePanel, buttons),
            };

            Loaded += async (_, _) =>
            {
                if (AutoStart)
                {
                    await StartAsync();
                }
            };
            Closing += (_, _) =>
            {
                codeDecision?.TrySetResult(false);
                cts.Cancel();
                discoveryCts?.Cancel();
            };
        }

        /// <summary>False only for rendering the window without starting the network side.</summary>
        internal bool AutoStart { get; init; } = true;

        /// <summary>The settings saved by a successful pairing (null if nothing changed).</summary>
        internal LiteConfig SavedConfig { get; private set; }

        private async Task StartAsync()
        {
            if (host)
            {
                primary.Visibility = Visibility.Collapsed;
                await RunHostAsync();
            }
            else
            {
                primary.IsDefault = true;
                UpdateJoinButton();
                StartDiscovery();
            }
        }

        // ---- host -----------------------------------------------------------------------------------------

        private async Task RunHostAsync()
        {
            var cfg = LiteConfig.TryLoad(settingsPath, out var errors, out _);
            if (cfg == null)
            {
                Finish("Fix this PC's settings first:\n" + string.Join("\n", errors), false);
                return;
            }

            await RunAsync(async () =>
            {
                string joined = await PairingSession.HostAsync(cfg, Environment.MachineName, this, SaveHost, PairingDefaults.Port, cts.Token);
                return joined == null
                    ? null
                    : $"{joined} was added. Both PCs now share the security key and the layout ({string.Join(", ", SavedConfig.Machines.Where(m => m.Length > 0))}).";
            });
        }

        private void SaveHost(List<string> machines)
        {
            var cfg = LiteConfig.TryParse(settingsPath, out string error) ?? throw new PairingException(error);
            cfg.Machines = machines;
            Save(cfg);
        }

        // ---- join -----------------------------------------------------------------------------------------

        private void StartDiscovery()
        {
            SetStatus($"Looking for PCs in \"Add a PC\" mode… On the PC that is already set up, open MwbLite Settings > Pairing and press Add a PC.\nThis PC: {LiteConfig.NormalizeName(Environment.MachineName)}");
            discoveryCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            _ = PairingDiscovery.SearchAsync(
                h => Dispatcher.BeginInvoke(() =>
                {
                    if (found.Any(f => f.Name == h.Name && f.Address.Equals(h.Address)))
                    {
                        return;
                    }

                    found.Add(h);
                    hostItems.Add($"{h.Name}   ({LiteStatus.Display(h.Address)})");
                    if (hostsList.SelectedIndex < 0 && manualHost.Text.Length == 0)
                    {
                        hostsList.SelectedIndex = 0;
                    }
                }),
                PairingDefaults.Port,
                discoveryCts.Token);
        }

        private void UpdateJoinButton() => primary.IsEnabled = hostsList.SelectedIndex >= 0 || manualHost.Text.Trim().Length > 0;

        private async void StartJoin()
        {
            if (!primary.IsEnabled || joinPanel.Visibility != Visibility.Visible)
            {
                return;
            }

            string address;
            int port = PairingDefaults.Port;
            if (manualHost.Text.Trim().Length > 0)
            {
                address = manualHost.Text.Trim();
            }
            else
            {
                DiscoveredHost h = found[hostsList.SelectedIndex];
                address = h.Address.ToString();
                port = h.Port;
            }

            discoveryCts?.Cancel();
            joinPanel.Visibility = Visibility.Collapsed;
            primary.Visibility = Visibility.Collapsed;

            await RunAsync(async () =>
            {
                string hostName = await PairingSession.JoinAsync(address, port, Environment.MachineName, this, SaveJoiner, cts.Token);
                return hostName == null
                    ? null
                    : $"Joined {hostName}. This PC now uses its security key and layout ({string.Join(", ", SavedConfig.Machines.Where(m => m.Length > 0))}).";
            });
        }

        private void SaveJoiner(PairingPayload payload)
        {
            var cfg = File.Exists(settingsPath) ? LiteConfig.TryParse(settingsPath, out _) ?? LiteConfig.CreateDefault() : LiteConfig.CreateDefault();
            cfg.SecurityKey = payload.SecurityKey;
            cfg.Machines = payload.Machines;
            cfg.Layout.OneRow = payload.OneRow;
            cfg.Layout.WrapMouse = payload.WrapMouse;
            cfg.Advanced.TcpPort = payload.TcpPort;
            Save(cfg);
        }

        // ---- shared ---------------------------------------------------------------------------------------

        private void Save(LiteConfig cfg)
        {
            cfg.Normalize();
            var errors = cfg.Validate(out _);
            if (errors.Count > 0)
            {
                throw new PairingException("The resulting settings are not valid: " + errors[0]);
            }

            cfg.Save(settingsPath);
            SavedConfig = cfg;
            LiteLog.Write(nameof(PairingWindow), (host ? "Add a PC" : "Join") + ": saved " + settingsPath);
        }

        private async Task RunAsync(Func<Task<string>> session)
        {
            try
            {
                string done = await session();
                Finish(done ?? "Pairing cancelled. Nothing was changed.", done != null);
            }
            catch (PairingException e)
            {
                Finish(e.Message, false);
            }
            catch (OperationCanceledException)
            {
                // window closed
            }
            catch (Exception e) when (e is IOException or SocketException or CryptographicException or ObjectDisposedException)
            {
                if (!cts.IsCancellationRequested)
                {
                    LiteLog.Write(nameof(PairingWindow), "Pairing failed: " + e);
                    Finish("Pairing failed: " + e.Message, false);
                }
            }
        }

        public void Status(string text) => SetStatus(text);

        public Task<bool> ConfirmCodeAsync(string peerName, string code, CancellationToken cancellationToken)
        {
            var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() =>
            {
                if (decision.TrySetResult(false))
                {
                    Dispatcher.BeginInvoke(() => codePanel.Visibility = Visibility.Collapsed);
                }
            });

            Dispatcher.BeginInvoke(() =>
            {
                codeDecision = decision;
                status.Text = string.Empty;
                codePrompt.Text = host
                    ? $"{peerName} wants to join. Check that {peerName} shows the same code:"
                    : $"Check that {peerName} shows the same code:";
                codeLabel.Text = code[..3] + " " + code[3..];
                codePanel.Visibility = Visibility.Visible;
                primary.Content = "Codes match";
                primary.Visibility = Visibility.Visible;
                primary.IsEnabled = true;
                primary.IsDefault = false; // a deliberate click, not Enter
                _ = Activate();
            });

            return decision.Task;
        }

        private void OnPrimary()
        {
            if (finished)
            {
                Close();
            }
            else if (codeDecision != null && !codeDecision.Task.IsCompleted)
            {
                primary.IsEnabled = false;
                codeDecision.TrySetResult(true);
            }
            else if (!host)
            {
                StartJoin();
            }
        }

        private void OnCancel()
        {
            if (codeDecision != null && !codeDecision.Task.IsCompleted)
            {
                codeDecision.TrySetResult(false); // tells the other PC, then finishes
                return;
            }

            Close();
        }

        private void Finish(string message, bool success)
        {
            Dispatcher.BeginInvoke(() =>
            {
                finished = true;
                discoveryCts?.Cancel();
                joinPanel.Visibility = Visibility.Collapsed;
                codePanel.Visibility = Visibility.Collapsed;
                status.Text = message;
                status.SetResourceReference(TextBlock.ForegroundProperty, success ? "TextFillColorPrimaryBrush" : "SystemFillColorCriticalBrush");
                primary.Content = "Close";
                primary.Visibility = Visibility.Visible;
                primary.IsEnabled = true;
                primary.IsDefault = true;
                cancel.Visibility = Visibility.Collapsed;
            });
        }

        private void SetStatus(string text) => Dispatcher.BeginInvoke(() => status.Text = text);
    }
}
