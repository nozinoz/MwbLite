// MwbLite Settings > PCs > Line up screens: one row (EdgeAlignmentRow) per pair of neighbouring PCs in the layout being
// edited, for the screens of different sizes (see LiteEdgeAlignment). The entries are saved with Apply like every other
// setting. "Use real sizes" asks both PCs for their physical screen size over the control channel (LiteControl).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace MouseWithoutBorders.Lite.Ui
{
    internal sealed partial class SettingsWindow
    {
        private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(8);

        /// <summary>Rebuilds the rows when the neighbouring pairs of the edited layout changed (or when forced).</summary>
        private void RebuildAlignment(bool force)
        {
            List<EdgePair> pairs = LiteEdgeAlignment.Neighbours(layout.Slots, layout.OneRow);
            string signature = string.Join("|", pairs.Select(p => p.From + (p.Horizontal ? ">" : "v") + p.To));
            if (!force && signature == alignmentSignature)
            {
                return;
            }

            alignmentSignature = signature;
            alignmentList.Children.Clear();
            if (pairs.Count == 0)
            {
                alignmentList.Children.Add(WithTopMargin(Kit.Hint("Put at least two PCs next to each other in the layout above to line up their screens."), 10));
                return;
            }

            bool first = true;
            foreach (EdgePair pair in pairs)
            {
                EdgeAlignmentEntry entry = LiteEdgeAlignment.Find(alignment, pair.From, pair.To);
                var row = new EdgeAlignmentRow(pair, entry?.Start ?? 0, entry?.End ?? 1, OnAlignmentChanged, r => _ = UseRealSizesAsync(r));
                if (entry != null && (row.Start != entry.Start || row.End != entry.End))
                {
                    OnAlignmentChanged(pair, row.Start, row.End); // the row clamped a value that was out of range
                }

                if (!first)
                {
                    var line = new Border { Height = 1, Margin = new Thickness(0, 16, 0, 0) };
                    line.SetResourceReference(Border.BackgroundProperty, "CardStrokeColorDefaultBrush");
                    alignmentList.Children.Add(line);
                }

                row.Margin = new Thickness(0, 16, 0, 0);
                alignmentList.Children.Add(row);
                first = false;
            }

            UpdateRealSizeAvailability();
        }

        private void OnAlignmentChanged(EdgePair pair, double start, double end)
        {
            EdgeAlignmentEntry entry = LiteEdgeAlignment.Find(alignment, pair.From, pair.To);
            if (entry == null)
            {
                entry = new EdgeAlignmentEntry { From = pair.From, To = pair.To };
                alignment.Add(entry);
            }

            entry.Start = start;
            entry.End = end;
            if (entry.IsDefault)
            {
                _ = alignment.Remove(entry);
            }
        }

        // "Use real sizes" needs both PCs to answer, so it is only offered while they are connected.
        private void UpdateRealSizeAvailability()
        {
            foreach (EdgeAlignmentRow row in alignmentList.Children.OfType<EdgeAlignmentRow>())
            {
                string whyNot = null;
                if (LastStatus == null)
                {
                    whyNot = "MwbLite isn't running on this PC, so it can't ask the other PC for its screen size.";
                }
                else
                {
                    foreach (string name in new[] { row.Pair.From, row.Pair.To })
                    {
                        PcState? state = name.Equals(ThisPc, StringComparison.OrdinalIgnoreCase)
                            ? PcState.ThisPc
                            : LastStatus.Pcs.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.State;
                        if (state is not (PcState.ThisPc or PcState.Connected))
                        {
                            whyNot = name + " isn't connected, so it can't tell its screen size. Connect it first, or line the screens up by hand.";
                            break;
                        }
                    }
                }

                row.SetRealSizesAvailable(whyNot == null, whyNot);
            }
        }

        private async Task UseRealSizesAsync(EdgeAlignmentRow row)
        {
            EdgePair pair = row.Pair;
            row.SetBusy(true, "Asking " + pair.From + " and " + pair.To + " for their screen size…");
            try
            {
                using var cts = new CancellationTokenSource(HelloTimeout);
                Task<(double Width, double Height)?> fromTask = ScreenSizeAsync(pair.From, cts.Token);
                Task<(double Width, double Height)?> toTask = ScreenSizeAsync(pair.To, cts.Token);
                (double Width, double Height)? from = await fromTask;
                (double Width, double Height)? to = await toTask;

                string missing = from == null ? pair.From : to == null ? pair.To : null;
                if (missing != null)
                {
                    row.SetBusy(false, missing + " didn't tell its screen size. Check that it's connected and runs MwbLite 0.9 or later, and that Windows knows the screen's size; or line the screens up by hand.");
                    return;
                }

                // The edge they share: heights for PCs side by side, widths for PCs one above the other.
                double fromMm = pair.Horizontal ? from.Value.Height : from.Value.Width;
                double toMm = pair.Horizontal ? to.Value.Height : to.Value.Width;
                var span = LiteEdgeAlignment.FromRealSizes(fromMm, toMm);
                if (span == null)
                {
                    row.SetBusy(false, "The sizes don't make sense; line the screens up by hand.");
                    return;
                }

                row.SetSpan(span.Value.Start, span.Value.End, raise: true);
                string dimension = pair.Horizontal ? "tall" : "wide";
                row.SetBusy(false, $"Lined up to scale: {pair.From} is {fromMm:0} mm {dimension}, {pair.To} {toMm:0} mm. Apply to save.");
                SetMessage("Screens lined up to scale. Press Apply to keep it.", ok: true);
            }
            catch (Exception e)
            {
                LiteLog.Write(nameof(SettingsWindow), "Use real sizes failed: " + e.Message);
                row.SetBusy(false, "Couldn't ask the PCs: " + e.Message);
            }
        }

        // A PC's physical screen size in mm: read here for this PC, asked over the control channel for the others.
        private async Task<(double Width, double Height)?> ScreenSizeAsync(string pc, CancellationToken ct)
        {
            try
            {
                if (pc.Equals(ThisPc, StringComparison.OrdinalIgnoreCase))
                {
                    return LiteNative.TryGetPrimaryScreenSizeMm(out int w, out int h) ? (w, h) : null;
                }

                PeerInfo info = await LiteControl.HelloAsync(pc, ct);
                if (info == null || info.ScreenHeightMm <= 0)
                {
                    return null;
                }

                // The reply carries the height in mm; the width follows from the pixel shape (square pixels).
                double width = info.ScreenHeight > 0 ? (double)info.ScreenHeightMm * info.ScreenWidth / info.ScreenHeight : 0;
                return (width, info.ScreenHeightMm);
            }
            catch (Exception e)
            {
                LiteLog.Write(nameof(SettingsWindow), "No screen size from " + pc + ": " + e.Message);
                return null;
            }
        }
    }
}
