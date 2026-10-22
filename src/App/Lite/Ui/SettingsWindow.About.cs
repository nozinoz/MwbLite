// MwbLite Settings > About: version and build details, links to the project, the update status (from the
// project's GitHub Releases) with release notes, and Update, which downloads and verifies the new Setup and runs it.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

using WpfClipboard = System.Windows.Clipboard;
using WpfMessageBox = System.Windows.MessageBox;

namespace MouseWithoutBorders.Lite.Ui
{
    internal sealed partial class SettingsWindow
    {
        private CancellationTokenSource updateCts;

        /// <summary>Preview (README images): no network, neutral details.</summary>
        internal static bool PreviewMode { get; set; }

        private FrameworkElement BuildAbout()
        {
            var version = new TextBlock { Text = "Version " + LiteUpdate.CurrentText, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 0) };
            var header = Kit.Horizontal(
                Kit.Logo(72),
                Kit.Stack(
                    new TextBlock { Text = "MwbLite", FontSize = 30, FontWeight = FontWeights.SemiBold },
                    version,
                    Kit.Hint("One keyboard, mouse and clipboard for all your Windows PCs. Based on Mouse Without Borders from Microsoft PowerToys.")));
            ((FrameworkElement)header.Children[1]).Margin = new Thickness(18, 0, 0, 0);
            ((FrameworkElement)header.Children[1]).MaxWidth = 520;

            checkUpdateButton = Kit.Button("Check for updates", (_, _) => _ = CheckForUpdateAsync(userAsked: true));
            checkUpdateButton.Margin = new Thickness(0);
            updateButton = Kit.Button("Update now", (_, _) => _ = UpdateAsync(), accent: true);
            updateButton.Visibility = Visibility.Collapsed;
            var notesLink = Link("Release notes", () => Open(latestRelease?.PageUrl ?? LiteUpdate.ReleasesUrl));
            notesLink.Margin = new Thickness(14, 0, 0, 0);
            var buttons = Kit.Horizontal(checkUpdateButton, updateButton, notesLink);
            buttons.Margin = new Thickness(0, 12, 0, 0);
            releaseNotes.FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI");

            var updates = Kit.Card(Kit.Stack(updateStatus, updateDetail, buttons, updateProgress, releaseNotes));
            ShowUpdateState(LiteUpdate.ReadState());

            var copy = Kit.Button("Copy details", (_, _) =>
            {
                WpfClipboard.SetText(DetailsText());
                SetMessage("Details copied to the clipboard.", ok: true);
            });
            copy.Margin = new Thickness(0, 12, 0, 0);
            copy.HorizontalAlignment = HorizontalAlignment.Left;

            var page = Kit.Stack(
                Kit.PageTitle("About"),
                Kit.Card(header),
                Kit.Section("Updates"),
                updates,
                Kit.Row("", "Check for updates every week", "Asks GitHub once a week whether a new version is out and shows a notification. Nothing about you or this PC is sent.", checkForUpdates),
                Kit.Section("Details"),
                Kit.Card(Kit.Stack(DetailsGrid(), copy)),
                Kit.Section("Links"),
                Kit.Card(Kit.Stack(
                    LinkRow("", "Project on GitHub", LiteUpdate.RepoUrl),
                    LinkRow("", "All versions and release notes", LiteUpdate.ReleasesUrl),
                    LinkRow("", "Report a problem or suggest an idea", LiteUpdate.IssuesUrl),
                    LinkRow("", "Licence (MIT)", Path.Combine(LitePaths.ExeDir, "LICENSE")),
                    LinkRow("", "Third-party notices", Path.Combine(LitePaths.ExeDir, "NOTICE.md")))));

            // Look once when the page is first shown, unless it was checked recently.
            page.IsVisibleChanged += (_, e) =>
            {
                if ((bool)e.NewValue && !PreviewMode && latestRelease == null && DateTime.UtcNow - LiteUpdate.ReadState().LastCheckUtc > TimeSpan.FromHours(6))
                {
                    _ = CheckForUpdateAsync(userAsked: false);
                }
            };
            return page;
        }

        private Grid DetailsGrid()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int row = 0;
            foreach (var (label, value) in Details())
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var l = Kit.Hint(label);
                l.Margin = new Thickness(0, 3, 12, 3);
                var v = new TextBox { Text = value, IsReadOnly = true, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(0), Margin = new Thickness(0, 3, 0, 3) };
                Grid.SetRow(l, row);
                Grid.SetRow(v, row);
                Grid.SetColumn(v, 1);
                grid.Children.Add(l);
                grid.Children.Add(v);
                row++;
            }

            return grid;
        }

        private static (string Label, string Value)[] Details()
        {
            if (PreviewMode)
            {
                return new[]
                {
                    ("Version", LiteUpdate.CurrentText),
                    ("Running", "Service mode (controls the lock screen and UAC prompts)"),
                    ("This PC", ThisPc),
                    (".NET", RuntimeInformation.FrameworkDescription),
                    ("Windows", "Microsoft Windows 10.0.26100 X64"),
                    ("Installed in", @"C:\Program Files\MwbLite"),
                    ("Settings file", @"%LOCALAPPDATA%\MwbLite\settings.json"),
                    ("Logs", @"%LOCALAPPDATA%\MwbLite\logs"),
                    ("Licence", "MIT. Mouse Without Borders © Microsoft Corporation; MwbLite changes by its authors."),
                };
            }

            StatusSnapshot status = StatusSource();
            string mode = status == null ? "Not running" : status.ServiceMode ? "Service mode (controls the lock screen and UAC prompts)" : "Normal (runs as " + Environment.UserName + ")";
            return new[]
            {
                ("Version", LiteUpdate.CurrentText + (LiteUpdate.Commit.Length > 0 ? " (build " + LiteUpdate.Commit + ")" : string.Empty)),
                ("Running", mode),
                ("This PC", ThisPc),
                (".NET", RuntimeInformation.FrameworkDescription),
                ("Windows", RuntimeInformation.OSDescription + " " + RuntimeInformation.OSArchitecture),
                ("Installed in", LitePaths.ExeDir),
                ("Settings file", LitePaths.UserSettingsFile),
                ("Logs", Path.Combine(LitePaths.UserDir, "logs") + (status?.ServiceMode == true ? "  and  " + Path.Combine(LitePaths.ServiceDir, "logs") + " (administrators)" : string.Empty)),
                ("Licence", "MIT. Mouse Without Borders © Microsoft Corporation; MwbLite changes by its authors."),
            };
        }

        private static string DetailsText()
        {
            var sb = new StringBuilder("MwbLite\r\n");
            foreach (var (label, value) in Details())
            {
                sb.Append(label).Append(": ").Append(value).Append("\r\n");
            }

            return sb.ToString();
        }

        // ---- update status --------------------------------------------------------------------------------

        private void ShowUpdateState(LiteUpdate.CheckState state)
        {
            if (PreviewMode)
            {
                updateStatus.Text = "You're up to date";
                updateDetail.Text = LiteUpdate.CurrentText + " is the latest version. Checked just now.";
                return;
            }

            if (state.LastCheckUtc == default)
            {
                updateStatus.Text = "MwbLite " + LiteUpdate.CurrentText;
                updateDetail.Text = "Not checked for updates yet.";
                return;
            }

            Version seen = LiteUpdate.ParseVersion(state.LatestVersion);
            updateStatus.Text = seen != null && seen > LiteUpdate.Current
                ? $"Version {seen.ToString(3)} is available"
                : "You're up to date";
            updateDetail.Text = "Last checked " + When(state.LastCheckUtc) + ".";
        }

        private async Task CheckForUpdateAsync(bool userAsked)
        {
            updateCts?.Cancel();
            updateCts = new CancellationTokenSource();
            checkUpdateButton.IsEnabled = false;
            updateStatus.Text = "Checking for updates…";
            updateDetail.Text = string.Empty;
            try
            {
                ReleaseInfo r = await LiteUpdate.GetLatestAsync(updateCts.Token);
                latestRelease = r;
                if (r == null)
                {
                    updateStatus.Text = "You're up to date";
                    updateDetail.Text = "No releases have been published yet.";
                }
                else if (LiteUpdate.IsNewer(r))
                {
                    updateStatus.Text = $"Version {r.Version.ToString(3)} is available";
                    updateDetail.Text = $"You have {LiteUpdate.CurrentText}. Released {r.PublishedUtc.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture)}."
                        + (r.SetupUrl == null ? " This release has no Setup to download; get it from the release page." : " Update downloads Setup from GitHub, checks it, and installs it (one UAC prompt).");
                    updateButton.Visibility = r.SetupUrl == null ? Visibility.Collapsed : Visibility.Visible;
                }
                else
                {
                    updateStatus.Text = "You're up to date";
                    updateDetail.Text = $"{LiteUpdate.CurrentText} is the latest version (released {r.PublishedUtc.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture)}). Checked just now.";
                    updateButton.Visibility = Visibility.Collapsed;
                }

                if (r != null && !string.IsNullOrWhiteSpace(r.Notes))
                {
                    releaseNotes.Text = (LiteUpdate.IsNewer(r) ? "What's new in " : "Notes for ") + r.Version.ToString(3) + "\n\n" + PlainNotes(r.Notes);
                    releaseNotes.Visibility = Visibility.Visible;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e) when (e is System.Net.Http.HttpRequestException or System.Text.Json.JsonException or IOException)
            {
                updateStatus.Text = "Couldn't check for updates";
                updateDetail.Text = (userAsked ? "Check your internet connection and try again. " : string.Empty) + "(" + e.Message + ")";
            }
            finally
            {
                checkUpdateButton.IsEnabled = true;
            }
        }

        private async Task UpdateAsync()
        {
            ReleaseInfo r = latestRelease;
            if (r == null || !LiteUpdate.IsNewer(r))
            {
                return;
            }

            if (HasUnsavedChanges())
            {
                SetMessage("Save or undo your changes first, then update.", ok: false);
                return;
            }

            updateButton.IsEnabled = checkUpdateButton.IsEnabled = false;
            updateProgress.Visibility = Visibility.Visible;
            updateProgress.Value = 0;
            updateStatus.Text = $"Downloading MwbLite {r.Version.ToString(3)}…";
            updateCts = new CancellationTokenSource();
            try
            {
                var progress = new Progress<double>(f => updateProgress.Value = f);
                string setup = await LiteUpdate.DownloadSetupAsync(r, progress, updateCts.Token);
                updateStatus.Text = "Starting Setup…";
                updateDetail.Text = "MwbLite closes while Setup runs, then starts again.";
                LiteUpdate.RunSetup(setup);
                closingAfterSave = true; // nothing to save; don't ask
                Close();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e) when (e is System.Net.Http.HttpRequestException or IOException or InvalidOperationException or InvalidDataException or System.ComponentModel.Win32Exception)
            {
                updateStatus.Text = "The update didn't finish";
                updateDetail.Text = e.Message;
                updateProgress.Visibility = Visibility.Collapsed;
                updateButton.IsEnabled = checkUpdateButton.IsEnabled = true;
            }
        }

        // ---- helpers --------------------------------------------------------------------------------------

        private static string When(DateTime utc)
        {
            TimeSpan ago = DateTime.UtcNow - utc;
            return ago.TotalMinutes < 2 ? "just now"
                : ago.TotalHours < 1 ? $"{(int)ago.TotalMinutes} minutes ago"
                : ago.TotalDays < 1 ? $"{(int)ago.TotalHours} hours ago"
                : utc.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
        }

        /// <summary>Release notes as plain text (GitHub's are Markdown): drops heading marks and link syntax.</summary>
        internal static string PlainNotes(string markdown)
        {
            var sb = new StringBuilder();
            foreach (string raw in (markdown ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                bool bullet = line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal);
                if (bullet)
                {
                    line = line[2..];
                }

                line = line.TrimStart('#', ' ');
                line = System.Text.RegularExpressions.Regex.Replace(line, @"\[([^\]]+)\]\([^)]+\)", "$1");
                line = line.Replace("**", string.Empty).Replace("`", string.Empty);
                sb.Append(bullet ? "•  " + line : line).Append('\n');
            }

            return sb.ToString().Trim();
        }

        private static TextBlock Link(string text, Action click)
        {
            var link = new Hyperlink(new Run(text));
            link.Click += (_, _) => click();
            return new TextBlock(link) { VerticalAlignment = VerticalAlignment.Center };
        }

        private static FrameworkElement LinkRow(string icon, string text, string target)
        {
            var row = Kit.Horizontal(Kit.Icon(icon, 16), Link(text, () => Open(target)));
            ((FrameworkElement)row.Children[1]).Margin = new Thickness(12, 0, 0, 0);
            row.Margin = new Thickness(0, 5, 0, 5);
            row.ToolTip = target;
            return row;
        }

        private static void Open(string target)
        {
            try
            {
                if (File.Exists(target) && !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    _ = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe"), "\"" + target + "\"") { UseShellExecute = false });
                }
                else if (target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                }
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _ = WpfMessageBox.Show("Couldn't open " + target + ": " + e.Message, "MwbLite");
            }
        }
    }
}
