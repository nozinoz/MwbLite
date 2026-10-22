// MwbLite: file copies in progress, for the progress window, the tray and status.json.
//
// MWB moves one file per connection on its clipboard/file channel (folders and multi-file selections travel as one
// zip, see LiteFileBundle). The send loop (SocketStuff.SendFileEx) and the receive loop
// (Clipboard.ReceiveAndProcessClipboardDataCore) report here. Cancel closes the transfer's socket: both loops
// already treat that as a failed transfer, and the receiver deletes its partial file.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;

using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.Lite
{
    internal static class LiteTransfers
    {
        /// <summary>How long a finished transfer stays listed (the window shows the result briefly).</summary>
        internal static readonly TimeSpan KeepFinished = TimeSpan.FromSeconds(4);

        private static readonly Lock Gate = new();
        private static readonly List<Transfer> Items = new();
        private static int nextId;

        internal sealed class Transfer
        {
            private long total;
            private long done;

            internal Transfer(int id, string name, string peer, bool sending, long total, Socket socket, DateTime startedUtc)
            {
                Id = id;
                Name = name;
                Peer = peer ?? string.Empty;
                Sending = sending;
                this.total = total;
                Socket = socket;
                StartedUtc = startedUtc;
            }

            internal int Id { get; }

            internal string Name { get; set; }

            internal string Peer { get; set; }

            internal bool Sending { get; }

            internal Socket Socket { get; set; }

            internal DateTime StartedUtc { get; }

            /// <summary>Waiting for the folder/multi-file zip to be built.</summary>
            internal bool Preparing { get; set; }

            internal long Total
            {
                get => Interlocked.Read(ref total);
                set => Interlocked.Exchange(ref total, value);
            }

            internal long Done => Interlocked.Read(ref done);

            internal bool Succeeded { get; set; }

            internal bool Cancelled { get; set; }

            internal string Error { get; set; }

            /// <summary>Optional success message (e.g. "... is in Clipboard").</summary>
            internal string ResultText { get; set; }

            internal DateTime? EndedUtc { get; set; }

            internal bool IsActive => EndedUtc == null;

            internal void Report(long bytesDone) => Interlocked.Exchange(ref done, bytesDone);

            /// <summary>0..1, or -1 when the size isn't known yet.</summary>
            internal double Fraction => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : -1;

            internal double BytesPerSecond(DateTime nowUtc)
            {
                double seconds = ((EndedUtc ?? nowUtc) - StartedUtc).TotalSeconds;
                return seconds > 0.2 ? Done / seconds : 0;
            }

            internal string Direction => Sending ? "Sending to " + Peer : "Receiving from " + Peer;
        }

        internal static Transfer Begin(string name, string peer, bool sending, long total, Socket socket)
        {
            var t = new Transfer(Interlocked.Increment(ref nextId), DisplayName(name), peer, sending, total, socket, DateTime.UtcNow);
            lock (Gate)
            {
                Items.Add(t);
            }

            LiteLog.Write(nameof(LiteTransfers), $"#{t.Id} {(sending ? "send" : "receive")} {t.Name} ({total} bytes) {(sending ? "to" : "from")} {peer}");
            return t;
        }

        /// <summary>Marks the transfer finished (success if <see cref="Transfer.Succeeded"/> was set) and notifies the user.</summary>
        internal static void Complete(Transfer t)
        {
            if (t == null || t.EndedUtc != null)
            {
                return;
            }

            t.EndedUtc = DateTime.UtcNow;
            t.Socket = null;
            string outcome = t.Succeeded ? "done" : t.Cancelled ? "cancelled" : "failed: " + (t.Error ?? "connection closed");
            LiteLog.Write(nameof(LiteTransfers), $"#{t.Id} {outcome} ({t.Done} of {t.Total} bytes)");

            if (Setting.Values.ShowTransferProgress && !Setting.Values.ShowClipNetStatus && !t.Cancelled)
            {
                // (With "status messages" on, MWB shows its own balloons for the same events.)
                string text = t.Succeeded
                    ? t.ResultText ?? (t.Sending ? $"Sent {t.Name} to {t.Peer}." : $"Received {t.Name} from {t.Peer}.")
                    : $"Copying {t.Name} {(t.Sending ? "to" : "from")} {t.Peer} failed: {t.Error ?? "the connection was closed."}";
                Common.ShowToolTip(text, 5000, t.Succeeded ? ToolTipIcon.Info : ToolTipIcon.Warning, true);
            }
        }

        internal static void Fail(Transfer t, string error)
        {
            if (t != null && t.Error == null)
            {
                t.Error = error;
            }
        }

        internal static void Cancel(int id)
        {
            Transfer t;
            lock (Gate)
            {
                t = Items.FirstOrDefault(x => x.Id == id && x.IsActive);
            }

            if (t == null)
            {
                return;
            }

            t.Cancelled = true;
            LiteLog.Write(nameof(LiteTransfers), $"#{t.Id} cancel requested");
            try
            {
                t.Socket?.Close();
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
            }
        }

        /// <summary>Active transfers plus ones that finished within <see cref="KeepFinished"/>.</summary>
        internal static List<Transfer> Snapshot(DateTime nowUtc)
        {
            lock (Gate)
            {
                Items.RemoveAll(t => t.EndedUtc != null && nowUtc - t.EndedUtc.Value > KeepFinished);
                return Items.ToList();
            }
        }

        internal static string DisplayName(string name)
        {
            string file = Path.GetFileName(name ?? string.Empty);
            if (file.EndsWith(LiteFileBundle.ZipSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return file.Substring(0, file.Length - LiteFileBundle.ZipSuffix.Length);
            }

            return file.EndsWith(LiteFileBundle.ManifestExtension, StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(file)
                : file;
        }

        internal static string FormatBytes(long bytes) => bytes switch
        {
            >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.CurrentCulture) + " GB",
            >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.0", CultureInfo.CurrentCulture) + " MB",
            >= 1L << 10 => (bytes / 1024).ToString(CultureInfo.CurrentCulture) + " KB",
            _ => bytes.ToString(CultureInfo.CurrentCulture) + " bytes",
        };

        /// <summary>One line for the window and tray, e.g. "Receiving from LAPTOP1 · 12.3 MB of 40.0 MB · 8.1 MB/s".</summary>
        internal static string Describe(Transfer t, DateTime nowUtc)
        {
            if (t.EndedUtc != null)
            {
                return t.Succeeded ? (t.Sending ? "Sent to " : "Received from ") + t.Peer
                    : t.Cancelled ? "Cancelled"
                    : "Failed: " + (t.Error ?? "the connection was closed");
            }

            if (t.Preparing)
            {
                return "Preparing (zipping) to send to " + t.Peer + "…";
            }

            string size = t.Total > 0 ? FormatBytes(t.Done) + " of " + FormatBytes(t.Total) : FormatBytes(t.Done);
            double rate = t.BytesPerSecond(nowUtc);
            return t.Direction + " · " + size + (rate > 0 ? " · " + FormatBytes((long)rate) + "/s" : string.Empty);
        }
    }
}
