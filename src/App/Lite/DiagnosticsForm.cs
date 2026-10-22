// MwbLite: the "Check connection" window opened from Settings. Shows each check as it finishes, offers
// "Fix firewall" (one UAC prompt) when the firewall is the problem, and can copy the results as text.
using System;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MouseWithoutBorders.Lite
{
    internal sealed class DiagnosticsForm : System.Windows.Forms.Form
    {
        private readonly LiteConfig cfg;
        private readonly ListView list = new();
        private readonly Button fixFirewall = new();
        private readonly Button runAgain = new();
        private readonly Label summary = new();
        private CancellationTokenSource cts;

        private DiagnosticsForm(LiteConfig cfg)
        {
            this.cfg = cfg;
            Font = SystemFonts.MessageBoxFont;
            int em = TextRenderer.MeasureText("M", Font).Width;
            Text = "MwbLite – Check connection";
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(em * 52, em * 26);

            list.View = View.Details;
            list.FullRowSelect = true;
            list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            list.Columns.Add(string.Empty, em * 3);
            list.Columns.Add("Check", em * 11);
            list.Columns.Add("Result", em * 36);
            list.Dock = DockStyle.Fill;
            list.ShowItemToolTips = true;

            summary.AutoSize = true;
            summary.Padding = new Padding(0, em / 2, 0, em / 2);
            summary.Dock = DockStyle.Top;
            summary.MaximumSize = new Size(em * 50, 0);

            fixFirewall.Text = "Fix firewall…";
            fixFirewall.AutoSize = true;
            fixFirewall.Visible = false;
            fixFirewall.Click += (_, _) => FixFirewall();
            runAgain.Text = "Run again";
            runAgain.AutoSize = true;
            runAgain.Click += (_, _) => Start();
            var copy = new Button { Text = "Copy results", AutoSize = true };
            copy.Click += (_, _) => Clipboard.SetText(Report());
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
            CancelButton = close;

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, em / 2, 0, 0) };
            buttons.Controls.AddRange(new Control[] { close, copy, runAgain, fixFirewall });

            var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(em) };
            root.Controls.Add(list);
            root.Controls.Add(summary);
            root.Controls.Add(buttons);
            Controls.Add(root);

            Shown += (_, _) => Start();
            FormClosed += (_, _) => cts?.Cancel();
        }

        internal static void ShowFor(IWin32Window owner, LiteConfig cfg)
        {
            using var form = new DiagnosticsForm(cfg.Clone());
            _ = form.ShowDialog(owner);
        }

        private void Start()
        {
            cts?.Cancel();
            cts = new CancellationTokenSource();
            CancellationToken ct = cts.Token;
            list.Items.Clear();
            fixFirewall.Visible = false;
            runAgain.Enabled = false;
            summary.Text = "Checking…";

            _ = Task.Run(async () =>
            {
                try
                {
                    await LiteDiagnostics.RunAsync(cfg, item => Post(() => Add(item)), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception e)
                {
                    Post(() => Add(new CheckItem(CheckResult.Warning, "Check", "Stopped: " + e.Message)));
                }

                Post(Finish);
            });
        }

        private void Post(Action action)
        {
            if (!IsDisposed && IsHandleCreated)
            {
                _ = BeginInvoke(action);
            }
        }

        private void Add(CheckItem item)
        {
            var row = new ListViewItem(new[] { Symbol(item.Result), item.Title, item.Detail })
            {
                ToolTipText = item.Detail,
                Tag = item,
                ForeColor = item.Result switch
                {
                    CheckResult.Problem => Color.Firebrick,
                    CheckResult.Warning => Color.DarkGoldenrod,
                    _ => SystemColors.WindowText,
                },
            };
            list.Items.Add(row);
            fixFirewall.Visible |= item.FirewallFixable;
        }

        private void Finish()
        {
            runAgain.Enabled = true;
            var items = list.Items.Cast<ListViewItem>().Select(i => (CheckItem)i.Tag).ToList();
            int problems = items.Count(i => i.Result == CheckResult.Problem);
            int warnings = items.Count(i => i.Result == CheckResult.Warning);
            summary.Text = problems == 0 && warnings == 0 ? "Everything checked out."
                : problems > 0 ? $"{problems} problem(s) found. Hover over a row to read all of it; fix the first one, then Run again."
                : $"{warnings} thing(s) to look at, nothing blocking.";
            list.Columns[2].Width = -2; // fill
        }

        private void FixFirewall()
        {
            int rc = LiteCommands.RepairFirewall(cfg.Advanced.TcpPort);
            if (rc == 0)
            {
                Start();
            }
            else if (rc != 1223)
            {
                MessageBox.Show(this, "Couldn't change the firewall (exit code " + rc + ").", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static string Symbol(CheckResult r) => r switch
        {
            CheckResult.Ok => "✓",
            CheckResult.Warning => "!",
            CheckResult.Problem => "✗",
            _ => "i",
        };

        private string Report()
        {
            var sb = new StringBuilder("MwbLite connection check, " + DateTime.Now.ToString("g", System.Globalization.CultureInfo.CurrentCulture) + ", on " + Environment.MachineName + "\r\n");
            foreach (ListViewItem i in list.Items)
            {
                var item = (CheckItem)i.Tag;
                sb.Append('[').Append(item.Result).Append("] ").Append(item.Title).Append(": ").Append(item.Detail).Append("\r\n");
            }

            return sb.ToString();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                cts?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
