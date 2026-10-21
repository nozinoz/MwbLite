// MwbLite: the user-editable configuration (settings.json). Replaces PowerToys' MouseWithoutBordersSettings /
// MouseWithoutBordersProperties and the PowerToys Settings UI. MwbLite only ever *reads* this file, except
// to create it with defaults when it does not exist. Runtime values MWB persists (machine IDs, machine
// pool, cursor position...) go to state.json instead (see LiteState).
//
// Defaults below are the upstream MouseWithoutBordersProperties defaults.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

using Microsoft.PowerToys.Settings.UI.Library;
using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders.Lite
{
    internal sealed class LiteConfig
    {
        internal const int MaxMachines = 4; // MachineStuff.MAX_MACHINE

        internal static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };

        /// <summary>Pairing key. Must be identical on every machine; at least 16 characters (spaces ignored).</summary>
        public string SecurityKey { get; set; } = string.Empty;

        /// <summary>
        /// Up to 4 machine (computer) names. The order is the screen layout: left-to-right in one row, or a
        /// 2x2 grid (row-major) when layout.oneRow is false. Use "" for an empty slot. Keep this identical on
        /// every machine; MWB also syncs the layout between connected machines at runtime.
        /// </summary>
        public List<string> Machines { get; set; } = new();

        public LayoutConfig Layout { get; set; } = new();

        public bool ShareClipboard { get; set; } = true;

        public bool TransferFile { get; set; } = true;

        public bool StartWithWindows { get; set; } = true;

        /// <summary>Run through the MwbLite.Service Windows service (lock screen / UAC prompt support). Needs "--install".</summary>
        public bool UseService { get; set; }

        public AdvancedConfig Advanced { get; set; } = new();

        internal sealed class LayoutConfig
        {
            public bool OneRow { get; set; } = true;

            /// <summary>Wrap the cursor from the last screen to the first.</summary>
            public bool WrapMouse { get; set; } = true;
        }

        internal sealed class AdvancedConfig
        {
            public int TcpPort { get; set; } = 15100;

            public bool HideMouseAtScreenEdge { get; set; } = true;

            public bool DrawMouseCursor { get; set; } = true;

            public bool ValidateRemoteIp { get; set; }

            public bool SameSubnetOnly { get; set; }

            public bool BlockScreenSaverOnOtherMachines { get; set; } = true;

            public bool MoveMouseRelatively { get; set; }

            public bool BlockMouseAtScreenCorners { get; set; }

            public bool ShowClipboardAndNetworkStatusMessages { get; set; }

            public EasyMouseOption EasyMouse { get; set; } = EasyMouseOption.Enable;

            public bool DisableEasyMouseWhenForegroundWindowIsFullscreen { get; set; } = true;

            public List<string> EasyMouseFullscreenExcludedApps { get; set; } = new();

            /// <summary>Static name-to-IP mappings, each "NAME 192.168.1.20".</summary>
            public List<string> Name2Ip { get; set; } = new();

            public HotkeysConfig Hotkeys { get; set; } = new();
        }

        internal sealed class HotkeysConfig
        {
            /// <summary>Ctrl+Alt+{F1..F4} ("F1"), Ctrl+Alt+{1..4} ("1") or "disabled".</summary>
            public string SwitchMachine { get; set; } = "F1";

            public string ToggleEasyMouse { get; set; } = "Win+Ctrl+Alt+E";

            public string LockMachine { get; set; } = "Win+Ctrl+Alt+L";

            public string Reconnect { get; set; } = "Win+Ctrl+Alt+R";

            public string SwitchToAllPcs { get; set; } = string.Empty;
        }

        // ---- derived values -------------------------------------------------------------------

        /// <summary>The 4-slot machine matrix in MWB's format.</summary>
        internal string[] MatrixSlots()
        {
            var slots = new string[MaxMachines];
            for (int i = 0; i < MaxMachines; i++)
            {
                slots[i] = i < Machines.Count ? NormalizeName(Machines[i]) : string.Empty;
            }

            return slots;
        }

        internal int SwitchMachineHotkeyCode() => (Advanced.Hotkeys.SwitchMachine ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "F1" => (int)VK.F1,
            "1" => '1',
            _ => 0,
        };

        internal string Name2IpString() => string.Join("\r\n", Advanced.Name2Ip ?? new List<string>());

        internal static string NormalizeName(string name) => (name ?? string.Empty).Trim().ToUpper(CultureInfo.CurrentCulture);

        internal static string NormalizeKey(string key) => new string((key ?? string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray());

        // ---- load / validate / defaults -----------------------------------------------------

        internal static LiteConfig CreateDefault()
        {
            return new LiteConfig
            {
                SecurityKey = Encryption.CreateRandomKey(),
                Machines = new List<string> { NormalizeName(Environment.MachineName) },
            };
        }

        /// <summary>Loads and validates a config file. Returns null (and the reason) if it is unusable.</summary>
        internal static LiteConfig TryLoad(string path, out List<string> errors, out List<string> warnings)
        {
            errors = new List<string>();
            warnings = new List<string>();
            LiteConfig cfg;

            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                cfg = JsonSerializer.Deserialize<LiteConfig>(json, JsonOptions);
            }
            catch (JsonException e)
            {
                errors.Add($"{Path.GetFileName(path)} is not valid JSON: {e.Message}");
                return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                errors.Add($"Cannot read {path}: {e.Message}");
                return null;
            }

            if (cfg == null)
            {
                errors.Add($"{Path.GetFileName(path)} is empty.");
                return null;
            }

            cfg.Layout ??= new LayoutConfig();
            cfg.Advanced ??= new AdvancedConfig();
            cfg.Advanced.Hotkeys ??= new HotkeysConfig();
            cfg.Advanced.Name2Ip ??= new List<string>();
            cfg.Advanced.EasyMouseFullscreenExcludedApps ??= new List<string>();
            cfg.Machines = (cfg.Machines ?? new List<string>()).Select(NormalizeName).ToList();
            cfg.SecurityKey = NormalizeKey(cfg.SecurityKey);

            if (!Encryption.IsKeyValid(cfg.SecurityKey, out string keyError))
            {
                errors.Add("securityKey: " + keyError);
            }

            if (cfg.Machines.Count > MaxMachines)
            {
                errors.Add($"machines: at most {MaxMachines} entries are supported (found {cfg.Machines.Count}).");
            }

            var named = cfg.Machines.Where(m => m.Length > 0).ToList();
            if (named.Count != named.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            {
                errors.Add("machines: a machine name is listed twice.");
            }

            if (named.Any(m => m.Contains(',', StringComparison.Ordinal) || m.Contains(':', StringComparison.Ordinal)))
            {
                errors.Add("machines: names cannot contain ',' or ':'.");
            }

            if (!named.Contains(NormalizeName(Environment.MachineName), StringComparer.OrdinalIgnoreCase))
            {
                warnings.Add($"machines does not include this computer ({NormalizeName(Environment.MachineName)}); MWB will place it in the first empty slot.");
            }

            if (cfg.Advanced.TcpPort is < 1024 or > 65534)
            {
                errors.Add("advanced.tcpPort must be between 1024 and 65534 (MWB also uses tcpPort+1).");
            }

            foreach (var (name, value) in cfg.HotkeyStrings())
            {
                if (!TryParseHotkey(value, out _))
                {
                    errors.Add($"advanced.hotkeys.{name}: cannot parse \"{value}\" (example: \"Win+Ctrl+Alt+E\", or \"\" to disable).");
                }
            }

            if (cfg.SwitchMachineHotkeyCode() == 0 && !string.Equals(cfg.Advanced.Hotkeys.SwitchMachine?.Trim(), "disabled", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("advanced.hotkeys.switchMachine must be \"F1\", \"1\" or \"disabled\".");
            }

            foreach (string entry in cfg.Advanced.Name2Ip)
            {
                string[] parts = (entry ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !System.Net.IPAddress.TryParse(parts[1], out _))
                {
                    errors.Add($"advanced.name2Ip: \"{entry}\" must look like \"NAME 192.168.1.20\".");
                }
            }

            return errors.Count == 0 ? cfg : null;
        }

        /// <summary>Writes a default config if none exists. Never overwrites an existing file.</summary>
        internal static bool WriteDefaultIfMissing(string path)
        {
            if (File.Exists(path))
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string json = JsonSerializer.Serialize(CreateDefault(), JsonOptions);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(json);
            }

            return true;
        }

        internal LiteConfig Clone()
        {
            return JsonSerializer.Deserialize<LiteConfig>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions);
        }

        internal HotkeySettings Hotkey(string value) => TryParseHotkey(value, out var hk) ? hk : new HotkeySettings();

        private IEnumerable<(string Name, string Value)> HotkeyStrings()
        {
            yield return ("toggleEasyMouse", Advanced.Hotkeys.ToggleEasyMouse);
            yield return ("lockMachine", Advanced.Hotkeys.LockMachine);
            yield return ("reconnect", Advanced.Hotkeys.Reconnect);
            yield return ("switchToAllPcs", Advanced.Hotkeys.SwitchToAllPcs);
        }

        /// <summary>Parses "Win+Ctrl+Alt+Shift+Key" (any order, case-insensitive). "" means disabled.</summary>
        internal static bool TryParseHotkey(string value, out HotkeySettings hotkey)
        {
            hotkey = new HotkeySettings();
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            foreach (string raw in value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (raw.ToUpperInvariant())
                {
                    case "WIN":
                        hotkey.Win = true;
                        break;
                    case "CTRL":
                    case "CONTROL":
                        hotkey.Ctrl = true;
                        break;
                    case "ALT":
                        hotkey.Alt = true;
                        break;
                    case "SHIFT":
                        hotkey.Shift = true;
                        break;
                    default:
                        if (hotkey.Code != 0)
                        {
                            return false;
                        }

                        string key = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
                        if (!Enum.TryParse(key, ignoreCase: true, out Keys k) || k == Keys.None || (k & Keys.Modifiers) != 0)
                        {
                            return false;
                        }

                        hotkey.Code = (int)k;
                        break;
                }
            }

            return hotkey.Code != 0;
        }
    }
}
