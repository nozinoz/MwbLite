// MwbLite pairing window: "Add a PC" (host) or "Join" (joiner). See PairingSession / PairingCrypto.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MouseWithoutBorders.Lite.Pairing
{
    internal sealed class PairingForm : System.Windows.Forms.Form, IPairingUi
    {
        private readonly bool host;
        private readonly string settingsPath;
        private readonly CancellationTokenSource cts = new();
        private readonly int em;

        private readonly Label status = new();
        private readonly ListBox hostsList = new();
        private readonly TextBox manualHost = new();
        private readonly Panel joinPanel = new();
        private readonly Panel codePanel = new();
        private readonly Label codePrompt = new();
        private readonly Label codeLabel = new();
        private readonly Button primary = new();
        private readonly Button cancel = new();
        private readonly List<DiscoveredHost> found = new();

        private CancellationTokenSource discoveryCts;
        private TaskCompletionSource<bool> codeDecision;
        private bool finished;

        internal PairingForm(bool host, string settingsPath)
        {
            this.host = host;
            this.settingsPath = settingsPath;

            Font = SystemFonts.MessageBoxFont;
            em = TextRenderer.MeasureText("M", Font).Width;
            Text = host ? "Add a PC" : "Join";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            BuildLayout();
        }

        /// <summary>False only for rendering the window without starting the network side.</summary>
        internal bool AutoStart { get; init; } = true;

        /// <summary>The settings saved by a successful pairing (null if nothing changed).</summary>
        internal LiteConfig SavedConfig { get; private set; }

        private void BuildLayout()
        {
            var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(em), Dock = DockStyle.Fill };

            status.AutoSize = true;
            status.MaximumSize = new Size(em * 30, 0);
            status.Margin = new Padding(3, 3, 3, em);
            root.Controls.Add(status);

            // Join: PCs found on the network, or a typed name.
            var joinGrid = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
            hostsList.Width = em * 30;
            hostsList.Height = Font.Height * 6;
            hostsList.IntegralHeight = false;
            hostsList.SelectedIndexChanged += (s, e) => UpdateJoinButton();
            hostsList.DoubleClick += (s, e) => StartJoin();
            joinGrid.Controls.Add(hostsList);
            joinGrid.Controls.Add(new Label { Text = "Not listed? Type its computer name or IP address:", AutoSize = true, Margin = new Padding(3, em / 2, 3, 0) });
            manualHost.Width = em * 30;
            manualHost.TextChanged += (s, e) => UpdateJoinButton();
            joinGrid.Controls.Add(manualHost);
            joinPanel.AutoSize = true;
            joinPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            joinPanel.Controls.Add(joinGrid);
            joinPanel.Visible = !host;
            root.Controls.Add(joinPanel);

            // The code both PCs show.
            var codeGrid = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            codePrompt.AutoSize = true;
            codePrompt.MaximumSize = new Size(em * 30, 0);
            codeGrid.Controls.Add(codePrompt);
            codeLabel.AutoSize = true;
            codeLabel.Font = new Font(FontFamily.GenericSansSerif, Font.Size * 3, FontStyle.Bold);
            codeLabel.Margin = new Padding(3, em / 2, 3, em / 2);
            codeGrid.Controls.Add(codeLabel);
            codeGrid.Controls.Add(new Label { Text = "If the codes differ, press Cancel.", AutoSize = true, ForeColor = SystemColors.GrayText });
            codePanel.AutoSize = true;
            codePanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            codePanel.Controls.Add(codeGrid);
            codePanel.Visible = false;
            root.Controls.Add(codePanel);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = new Padding(0, em / 2, 0, 0) };
            foreach (var b in new[] { primary, cancel })
            {
                b.AutoSize = true;
                b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                b.MinimumSize = new Size(90, 0);
            }

            cancel.Text = "Cancel";
            cancel.Click += (s, e) => OnCancel();
            primary.Click += (s, e) => OnPrimary();
            buttons.Controls.AddRange(new Control[] { primary, cancel });
            root.Controls.Add(buttons);

            Controls.Add(root);
            CancelButton = cancel;
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!AutoStart)
            {
                return;
            }

            if (host)
            {
                primary.Visible = false;
                await RunHostAsync();
            }
            else
            {
                primary.Text = "Join";
                AcceptButton = primary;
                UpdateJoinButton();
                StartDiscovery();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            codeDecision?.TrySetResult(false);
            cts.Cancel();
            discoveryCts?.Cancel();
            base.OnFormClosing(e);
        }

        // ---- host ------------------------------------------------------------------------------

        private async Task RunHostAsync()
        {
            var cfg = LiteConfig.TryLoad(settingsPath, out var errors, out _);
            if (cfg == null)
            {
                Finish("Fix this PC's settings first:\r\n" + string.Join("\r\n", errors), false);
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

        // ---- join ------------------------------------------------------------------------------

        private void StartDiscovery()
        {
            SetStatus($"Looking for PCs in \"Add a PC\" mode… On the PC that is already set up, open MwbLite Settings and press Add a PC.\r\nThis PC: {LiteConfig.NormalizeName(Environment.MachineName)}");
            discoveryCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            _ = PairingDiscovery.SearchAsync(
                h =>
                {
                    if (IsDisposed)
                    {
                        return;
                    }

                    BeginInvoke(() =>
                    {
                        if (found.Any(f => f.Name == h.Name && f.Address.Equals(h.Address)))
                        {
                            return;
                        }

                        found.Add(h);
                        _ = hostsList.Items.Add($"{h.Name}   ({h.Address})");
                        if (hostsList.SelectedIndex < 0 && manualHost.Text.Length == 0)
                        {
                            hostsList.SelectedIndex = 0;
                        }
                    });
                },
                PairingDefaults.Port,
                discoveryCts.Token);
        }

        private void UpdateJoinButton()
        {
            primary.Enabled = hostsList.SelectedIndex >= 0 || manualHost.Text.Trim().Length > 0;
        }

        private async void StartJoin()
        {
            if (!primary.Enabled || joinPanel.Visible == false)
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
                var h = found[hostsList.SelectedIndex];
                address = h.Address.ToString();
                port = h.Port;
            }

            discoveryCts?.Cancel();
            joinPanel.Visible = false;
            primary.Visible = false;

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

        // ---- shared ----------------------------------------------------------------------------

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
            LiteLog.Write(nameof(PairingForm), (host ? "Add a PC" : "Join") + ": saved " + settingsPath);
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
                    LiteLog.Write(nameof(PairingForm), "Pairing failed: " + e);
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
                if (decision.TrySetResult(false) && !IsDisposed)
                {
                    BeginInvoke(() => codePanel.Visible = false);
                }
            });

            RunOnUi(() =>
            {
                codeDecision = decision;
                SetStatus(string.Empty);
                codePrompt.Text = host
                    ? $"{peerName} wants to join. Check that {peerName} shows the same code:"
                    : $"Check that {peerName} shows the same code:";
                codeLabel.Text = code[..3] + " " + code[3..];
                codePanel.Visible = true;
                primary.Text = "Codes match";
                primary.Visible = true;
                primary.Enabled = true;
                AcceptButton = null; // a deliberate click, not Enter
                Activate();
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
                primary.Enabled = false;
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
            RunOnUi(() =>
            {
                finished = true;
                discoveryCts?.Cancel();
                joinPanel.Visible = false;
                codePanel.Visible = false;
                status.ForeColor = success ? SystemColors.ControlText : Color.Firebrick;
                status.Text = message;
                primary.Text = "Close";
                primary.Visible = true;
                primary.Enabled = true;
                cancel.Visible = false;
                AcceptButton = primary;
            });
        }

        private void SetStatus(string text) => RunOnUi(() => status.Text = text);

        private void RunOnUi(Action action)
        {
            if (IsDisposed)
            {
                return;
            }

            if (InvokeRequired)
            {
                BeginInvoke(action);
            }
            else
            {
                action();
            }
        }
    }
}
