// MwbLite: lines up screens of different sizes. Pure logic (no I/O), shared by the engine, the config and the Settings
// window, and covered by the tests.
//
// MWB maps the cursor's position along the shared edge proportionally: the top of one screen lands on the top of the
// next. With a tall monitor beside a small laptop screen that sits lower on the desk, that is not where the cursor
// should come out. For each pair of neighbouring PCs (from, to), where "to" is right of / below "from", an entry says
// where the "to" screen's edge begins and ends along the "from" screen's edge, as fractions of the "from" edge:
// 0 is its top (left), 1 its bottom (right). The values go below 0 or above 1 when the "to" screen is bigger.
//
//     crossing from -> to:  t = clamp((f - start) / (end - start), 0, 1)     f, t: position along each screen's edge
//     crossing to -> from:  f = clamp(start + t * (end - start), 0, 1)
//
// No entry means 0..1, which is exactly MWB's proportional mapping. The coordinate that is mapped is the one along the
// edge: Y for left/right neighbours, X for up/down neighbours in the 2x2 layout. The engine applies it to the target
// point in MWB's universal 0..65535 space (MachineStuff.AlignEdge), so it works for absolute and relative mouse mode.
//
// Entries are in settings.json under advanced.edgeAlignment and are kept the same on every PC (LiteAlignmentSync):
// in relative mouse mode the PC the cursor is on computes the crossing.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace MouseWithoutBorders.Lite
{
    /// <summary>The direction the cursor crosses in.</summary>
    internal enum EdgeDirection
    {
        Left,
        Right,
        Up,
        Down,
    }

    /// <summary>Where the "to" screen sits along the "from" screen's edge (see the file header).</summary>
    internal sealed class EdgeAlignmentEntry
    {
        /// <summary>The left (or upper) PC.</summary>
        public string From { get; set; } = string.Empty;

        /// <summary>The right (or lower) PC.</summary>
        public string To { get; set; } = string.Empty;

        /// <summary>Where the "to" screen's edge begins, as a fraction of the "from" edge.</summary>
        public double Start { get; set; }

        /// <summary>Where the "to" screen's edge ends, as a fraction of the "from" edge.</summary>
        public double End { get; set; } = 1;

        internal bool IsDefault => Start == 0 && End == 1;

        internal EdgeAlignmentEntry Clone() => new() { From = From, To = To, Start = Start, End = End };
    }

    /// <summary>Two PCs that are neighbours in the layout. <see cref="Horizontal"/>: To is right of From (else below).</summary>
    internal readonly record struct EdgePair(string From, string To, bool Horizontal);

    internal static class LiteEdgeAlignment
    {
        internal const double MinValue = -4;
        internal const double MaxValue = 5;
        internal const double MinSpan = 0.05;
        internal const int MaxEntries = 8; // 4 PCs have at most 4 neighbouring pairs; a little slack

        private const double Universal = 65535;

        // ---- the mapping ----------------------------------------------------------------------------------

        /// <summary>Position along the "to" screen's edge for a position <paramref name="f"/> along the "from" screen's.</summary>
        internal static double ToTarget(double f, double start, double end) => Math.Clamp((f - start) / (end - start), 0, 1);

        /// <summary>Position along the "from" screen's edge for a position <paramref name="t"/> along the "to" screen's.</summary>
        internal static double ToSource(double t, double start, double end) => Math.Clamp(start + (t * (end - start)), 0, 1);

        /// <summary>The entry for exactly this pair (names compared like layout names), or null.</summary>
        internal static EdgeAlignmentEntry Find(IReadOnlyList<EdgeAlignmentEntry> entries, string from, string to)
        {
            if (entries == null || string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            {
                return null;
            }

            string f = LiteConfig.NormalizeName(from);
            string t = LiteConfig.NormalizeName(to);
            return entries.FirstOrDefault(e => e != null && LiteConfig.NormalizeName(e.From) == f && LiteConfig.NormalizeName(e.To) == t);
        }

        /// <summary>
        /// Maps the along-the-edge coordinate of a crossing's target point (MWB universal 0..65535) for a cursor leaving
        /// <paramref name="current"/> for <paramref name="next"/> in <paramref name="direction"/>. Without an entry for
        /// the pair the point is returned unchanged.
        /// </summary>
        internal static Point Apply(IReadOnlyList<EdgeAlignmentEntry> entries, string current, string next, EdgeDirection direction, Point universal)
        {
            bool forward = direction is EdgeDirection.Right or EdgeDirection.Down; // leaving "from" for "to"
            EdgeAlignmentEntry entry = forward ? Find(entries, current, next) : Find(entries, next, current);
            if (entry == null || entry.IsDefault || !IsUsable(entry))
            {
                return universal;
            }

            bool alongY = direction is EdgeDirection.Left or EdgeDirection.Right;
            double position = (alongY ? universal.Y : universal.X) / Universal;
            double mapped = forward ? ToTarget(position, entry.Start, entry.End) : ToSource(position, entry.Start, entry.End);
            int value = (int)Math.Round(mapped * Universal);
            return alongY ? new Point(universal.X, value) : new Point(value, universal.Y);
        }

        // ---- layout ---------------------------------------------------------------------------------------

        /// <summary>
        /// The neighbouring pairs MWB crosses between in a layout (4 slots, row-major): in one row each PC and the next
        /// one on its right (empty slots are skipped); in 2x2 the PCs side by side and one above the other.
        /// </summary>
        internal static List<EdgePair> Neighbours(IReadOnlyList<string> slots, bool oneRow)
        {
            var pairs = new List<EdgePair>();
            string[] s = Enumerable.Range(0, LiteConfig.MaxMachines)
                .Select(i => i < slots.Count ? LiteConfig.NormalizeName(slots[i]) : string.Empty)
                .ToArray();

            if (oneRow)
            {
                string[] named = s.Where(n => n.Length > 0).ToArray();
                for (int i = 0; i + 1 < named.Length; i++)
                {
                    pairs.Add(new EdgePair(named[i], named[i + 1], Horizontal: true));
                }

                return pairs;
            }

            // [0][1] / [2][3]
            void Add(int a, int b, bool horizontal)
            {
                if (s[a].Length > 0 && s[b].Length > 0)
                {
                    pairs.Add(new EdgePair(s[a], s[b], horizontal));
                }
            }

            Add(0, 1, true);
            Add(2, 3, true);
            Add(0, 2, false);
            Add(1, 3, false);
            return pairs;
        }

        /// <summary>The entries that still describe a neighbouring pair of the layout (others are ignored and not saved again).</summary>
        internal static List<EdgeAlignmentEntry> ForLayout(IEnumerable<EdgeAlignmentEntry> entries, IReadOnlyList<string> slots, bool oneRow)
        {
            List<EdgePair> pairs = Neighbours(slots, oneRow);
            var list = entries.ToList();
            return pairs
                .Select(p => Find(list, p.From, p.To))
                .Where(e => e != null && !e.IsDefault)
                .Select(e => e.Clone())
                .ToList();
        }

        // ---- validation and comparison --------------------------------------------------------------------

        /// <summary>The problem with an entry, or null if it is usable.</summary>
        internal static string Problem(EdgeAlignmentEntry e)
        {
            if (e == null || string.IsNullOrWhiteSpace(e.From) || string.IsNullOrWhiteSpace(e.To))
            {
                return "from and to must be PC names.";
            }

            if (!double.IsFinite(e.Start) || !double.IsFinite(e.End))
            {
                return "start and end must be numbers.";
            }

            if (e.Start < MinValue || e.Start > MaxValue || e.End < MinValue || e.End > MaxValue)
            {
                return $"start and end must be between {MinValue.ToString(CultureInfo.InvariantCulture)} and {MaxValue.ToString(CultureInfo.InvariantCulture)}.";
            }

            if (e.End - e.Start < MinSpan)
            {
                return "end must be at least " + MinSpan.ToString(CultureInfo.InvariantCulture) + " more than start.";
            }

            return null;
        }

        internal static bool IsUsable(EdgeAlignmentEntry e) => Problem(e) == null;

        /// <summary>A stable text for comparing two sets (names normalized, default entries and order ignored).</summary>
        internal static string Canonical(IEnumerable<EdgeAlignmentEntry> entries)
        {
            return string.Join(
                ";",
                (entries ?? Enumerable.Empty<EdgeAlignmentEntry>())
                    .Where(e => e != null && !e.IsDefault)
                    .Select(e => string.Join(",", LiteConfig.NormalizeName(e.From), LiteConfig.NormalizeName(e.To), e.Start.ToString("R", CultureInfo.InvariantCulture), e.End.ToString("R", CultureInfo.InvariantCulture)))
                    .OrderBy(s => s, StringComparer.Ordinal));
        }

        // ---- "Use real sizes" -----------------------------------------------------------------------------

        /// <summary>
        /// The span for two screens of known physical size along the shared edge (mm), centred on the "from" screen.
        /// Null if a size is unknown.
        /// </summary>
        internal static (double Start, double End)? FromRealSizes(double fromMm, double toMm)
        {
            if (!(fromMm > 0) || !(toMm > 0) || !double.IsFinite(fromMm) || !double.IsFinite(toMm))
            {
                return null;
            }

            double span = Math.Clamp(toMm / fromMm, MinSpan, MaxValue - MinValue);
            double start = Math.Clamp((1 - span) / 2, MinValue, MaxValue - span);
            return (Math.Round(start, 3), Math.Round(start + span, 3));
        }

        // ---- wire formats ---------------------------------------------------------------------------------

        /// <summary>The "alignment" control request's argument: {"entries":[{from,to,start,end}...]}.</summary>
        internal static JsonObject ToJson(IEnumerable<EdgeAlignmentEntry> entries)
        {
            var array = new JsonArray();
            foreach (EdgeAlignmentEntry e in entries)
            {
                array.Add(new JsonObject { ["from"] = e.From, ["to"] = e.To, ["start"] = e.Start, ["end"] = e.End });
            }

            return new JsonObject { ["entries"] = array };
        }

        /// <summary>Reads and checks a received request. Throws ArgumentException if anything is wrong.</summary>
        internal static List<EdgeAlignmentEntry> FromJson(JsonObject request)
        {
            if (request?["entries"] is not JsonArray array || array.Count > MaxEntries)
            {
                throw new ArgumentException("entries: expected a list of at most " + MaxEntries + ".");
            }

            var list = new List<EdgeAlignmentEntry>();
            foreach (JsonNode node in array)
            {
                try
                {
                    var e = new EdgeAlignmentEntry
                    {
                        From = LiteConfig.NormalizeName((string)node["from"]),
                        To = LiteConfig.NormalizeName((string)node["to"]),
                        Start = (double)node["start"],
                        End = (double)node["end"],
                    };

                    if (Problem(e) is string problem)
                    {
                        throw new ArgumentException(problem);
                    }

                    list.Add(e);
                }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException or NullReferenceException)
                {
                    throw new ArgumentException("entries: an entry is not {from, to, start, end}.");
                }
            }

            return list;
        }

        /// <summary>Compact text for a command line: "FROM:TO:START:END,FROM:TO:START:END"; "-" for none. PC names never contain ',' or ':'.</summary>
        internal static string Encode(IEnumerable<EdgeAlignmentEntry> entries)
        {
            string text = string.Join(",", entries.Select(e => string.Join(":", LiteConfig.NormalizeName(e.From), LiteConfig.NormalizeName(e.To), e.Start.ToString("R", CultureInfo.InvariantCulture), e.End.ToString("R", CultureInfo.InvariantCulture))));
            return text.Length == 0 ? "-" : text;
        }

        /// <summary>The reverse of <see cref="Encode"/>; null if the text is malformed.</summary>
        internal static List<EdgeAlignmentEntry> Decode(string text)
        {
            var list = new List<EdgeAlignmentEntry>();
            if (string.IsNullOrWhiteSpace(text) || text == "-")
            {
                return list;
            }

            foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] f = part.Split(':');
                if (f.Length != 4 ||
                    !double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double start) ||
                    !double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double end))
                {
                    return null;
                }

                var e = new EdgeAlignmentEntry { From = LiteConfig.NormalizeName(f[0]), To = LiteConfig.NormalizeName(f[1]), Start = start, End = end };
                if (!IsUsable(e) || list.Count >= MaxEntries)
                {
                    return null;
                }

                list.Add(e);
            }

            return list;
        }
    }
}
