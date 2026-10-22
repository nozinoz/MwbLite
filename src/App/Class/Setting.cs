// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;

using Microsoft.PowerToys.Settings.UI.Library;

// <summary>
//     Application settings.
// </summary>
// <history>
//     2008 created by Truong Do (ductdo).
//     2009-... modified by Truong Do (TruongDo).
//     2023- Included in PowerToys.
//     MwbLite: backed by a local settings.json (LiteConfig, read-only to the app) plus state.json
//              (LiteState, runtime values). The PowerToys settings library, its file watcher and all
//              Group Policy (GPOWrapper) overrides were removed. Member names are unchanged so the rest
//              of the upstream code compiles as-is.
// </history>
using MouseWithoutBorders.Core;
using MouseWithoutBorders.Lite;

using Lock = System.Threading.Lock;

[module: SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Scope = "member", Target = "MouseWithoutBorders.Properties.Setting.Values.#LoadIntSetting(System.String,System.Int32)", Justification = "Dotnet port with style preservation")]

namespace MouseWithoutBorders.Class
{
    internal class Settings
    {
        internal bool Changed;

        private readonly Lock _loadingSettingsLock = new Lock();
        private readonly Lock _pairingKeyLock = new Lock();
        private readonly Lock _saveLock = new Lock();
        private readonly IPairingKeyCodec _pairingKeyCodec;
        private readonly string _settingsFile;
        private readonly string _stateFile;

        // Effective configuration. Values MWB changes at runtime (e.g. a machine matrix received from a
        // peer, Easy Mouse toggled by hotkey) are applied to this in-memory copy only; the user's
        // settings.json is never rewritten. "Reload config" replaces it from disk.
        private LiteConfig _config;
        private LiteState _state;

        internal static bool IsProtectedMode
        {
            get
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                return identity.IsSystem;
            }
        }

        // Avoid instantly saving every change to the file when updating properties.
        public bool PauseInstantSaving { get; set; }

        /// <summary>Problems found the last time settings.json was loaded (empty if it loaded cleanly).</summary>
        internal IReadOnlyList<string> ConfigErrors { get; private set; } = Array.Empty<string>();

        internal IReadOnlyList<string> ConfigWarnings { get; private set; } = Array.Empty<string>();

        internal bool CreatedDefaultConfig { get; private set; }

        internal string SettingsFile => _settingsFile;

        internal bool CheckForUpdates
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _config.CheckForUpdates;
                }
            }
        }

        internal bool AllowInsecureConnections
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _config.Advanced.AllowInsecureConnections;
                }
            }
        }

        internal bool ShowTransferProgress
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _config.ShowTransferProgress;
                }
            }
        }

        internal bool StartWithWindows
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _config.StartWithWindows;
                }
            }
        }

        internal Settings(IPairingKeyCodec pairingKeyCodec)
            : this(pairingKeyCodec, LitePaths.SettingsFile, LitePaths.StateFile)
        {
        }

        internal Settings(IPairingKeyCodec pairingKeyCodec, string settingsFile, string stateFile)
        {
            ArgumentNullException.ThrowIfNull(pairingKeyCodec);
            _pairingKeyCodec = pairingKeyCodec;
            _settingsFile = settingsFile;
            _stateFile = stateFile;
            _state = LiteState.Load(_stateFile);
            _config = LoadConfigForStartup();
        }

        private LiteConfig LoadConfigForStartup()
        {
            try
            {
                // The SYSTEM instances read a copy written by the elevated --apply-config command and
                // never create it themselves.
                if (!IsProtectedMode && LiteConfig.WriteDefaultIfMissing(_settingsFile))
                {
                    CreatedDefaultConfig = true;
                    LiteLog.Write(nameof(Settings), $"Created default {_settingsFile}");
                }
            }
            catch (Exception e)
            {
                LiteLog.Write(nameof(Settings), $"Cannot create default {_settingsFile}: {e.Message}");
            }

            var cfg = LiteConfig.TryLoad(_settingsFile, out var errors, out var warnings);
            ConfigErrors = errors;
            ConfigWarnings = warnings;
            foreach (string w in warnings)
            {
                LiteLog.Write(nameof(Settings), "Config warning: " + w);
            }

            if (cfg != null)
            {
                return cfg;
            }

            foreach (string err in errors)
            {
                LiteLog.Write(nameof(Settings), "Config error: " + err);
            }

            // Unusable config: run with defaults and a random, unshared key so nothing can connect
            // until the file is fixed and reloaded.
            return LiteConfig.CreateDefault();
        }

        /// <summary>
        /// Re-reads settings.json and applies it, following the same order upstream used when the
        /// PowerToys Settings app changed the file. Returns false (keeping the current settings) if the
        /// file is invalid.
        /// </summary>
        internal bool Reload(out IReadOnlyList<string> errors, out IReadOnlyList<string> warnings, out bool restartNeeded)
        {
            var cfg = LiteConfig.TryLoad(_settingsFile, out var errs, out var warns);
            errors = errs;
            warnings = warns;
            restartNeeded = false;
            ConfigErrors = errs;
            ConfigWarnings = warns;

            if (cfg == null)
            {
                LiteLog.Write(nameof(Settings), "Reload rejected: " + string.Join(" | ", errs));
                return false;
            }

            LiteConfig last;
            lock (_loadingSettingsLock)
            {
                last = _config;
                _config = cfg;
            }

            restartNeeded = last.Advanced.TcpPort != cfg.Advanced.TcpPort || last.UseService != cfg.UseService;

            bool shouldSendMachineMatrix = false;

            // Same as in CheckBoxCircle_CheckedChanged
            if (last.Layout.WrapMouse != cfg.Layout.WrapMouse || last.Layout.OneRow != cfg.Layout.OneRow)
            {
                shouldSendMachineMatrix = true;
            }

            // Same as CheckBoxDrawMouse_CheckedChanged
            if (last.Advanced.DrawMouseCursor != cfg.Advanced.DrawMouseCursor && !cfg.Advanced.DrawMouseCursor)
            {
                CustomCursor.ShowFakeMouseCursor(int.MinValue, int.MinValue);
            }

            if (!Enumerable.SequenceEqual(last.MatrixSlots(), cfg.MatrixSlots()))
            {
                MachineStuff.MachineMatrix = null; // Forces read next time it's needed.
                LiteLayoutSync.SeedPool(cfg.MatrixSlots());
                shouldSendMachineMatrix = true;
            }

            if (UpdateRuntimeKey())
            {
                SocketStuff.InvalidKeyFound = false;
                InitAndCleanup.ReopenSocketDueToReadError = true;
                Common.ReopenSockets(true);
            }

            if (shouldSendMachineMatrix)
            {
                MachineStuff.SendMachineMatrix();
            }

            LiteLog.Write(nameof(Settings), "Settings reloaded from " + _settingsFile);
            return true;
        }

        public void SaveSettings()
        {
            // Only runtime state is persisted; the logon-desktop instance never writes (same rule as upstream).
            if (Common.RunOnLogonDesktop)
            {
                return;
            }

            LiteState snapshot;
            lock (_loadingSettingsLock)
            {
                snapshot = _state.Clone();
            }

            _ = Task.Factory.StartNew(
                () =>
            {
                for (int i = 0; i < 5; ++i)
                {
                    try
                    {
                        lock (_saveLock)
                        {
                            snapshot.Save(_stateFile);
                        }

                        return;
                    }
                    catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                    {
                        LiteLog.Write(nameof(Settings), $"Failed to write state: {ex.Message}");
                    }

                    System.Threading.Thread.Sleep(500);
                }
            },
                System.Threading.CancellationToken.None,
                TaskCreationOptions.None,
                TaskScheduler.Default);
        }

        internal string Username { get; set; }

        internal bool IsMyKeyRandom { get; set; }

        internal string MachineMatrixString
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return string.Join(",", _config.MatrixSlots());
                }
            }

            set => SetMachineMatrix(value.Split(","));
        }

        internal void SetMachineMatrix(IEnumerable<string> machineNames)
        {
            string[] names = machineNames.Select(LiteConfig.NormalizeName).ToArray();
            lock (_loadingSettingsLock)
            {
                // Runtime change (normally a layout received from a peer).
                _config.Machines = names.ToList();
            }

            // Live instance only: saved to the settings file after a short delay and pre-loaded into the machine pool.
            RuntimeLayoutChanged?.Invoke(names);
        }

        /// <summary>Set on Setting.Values only (see LiteLayoutSync); test instances have no side effects.</summary>
        internal Action<string[]> RuntimeLayoutChanged { get; init; }

        internal (string[] Machines, bool OneRow, bool WrapMouse) LayoutSnapshot()
        {
            lock (_loadingSettingsLock)
            {
                return (_config.MatrixSlots(), _config.Layout.OneRow, _config.Layout.WrapMouse);
            }
        }

        internal string MachinePoolString
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _state.MachinePool;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    if (!value.Equals(_state.MachinePool, StringComparison.OrdinalIgnoreCase))
                    {
                        _state.MachinePool = value;
                    }
                }
            }
        }

        internal string MyID => Application.ProductName + " Application";

        internal string MyIdEx => Application.ProductName + " Application-Ex";

        internal bool ShareClipboard
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _config.ShareClipboard;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _config.ShareClipboard = value;
                }
            }
        }

        // MwbLite: there is no policy layer; kept (always false) for the upstream settings forms.
        internal bool ShareClipboardIsGpoConfigured => false;

        internal bool TransferFile
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _config.TransferFile;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _config.TransferFile = value;
                }
            }
        }

        internal bool TransferFileIsGpoConfigured => false;

        internal bool MatrixOneRow
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _config.Layout.OneRow;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _config.Layout.OneRow = value;
                }
            }
        }

        internal bool MatrixCircle
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _config.Layout.WrapMouse;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _config.Layout.WrapMouse = value;
                }
            }
        }

        internal int EasyMouse
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return (int)_config.Advanced.EasyMouse;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    // Toggled by hotkey: kept until restart or "Reload config".
                    _config.Advanced.EasyMouse = (EasyMouseOption)value;
                }
            }
        }

        internal bool BlockMouseAtCorners
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _config.Advanced.BlockMouseAtScreenCorners;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _config.Advanced.BlockMouseAtScreenCorners = value;
                }
            }
        }

        internal bool DisableEasyMouseWhenForegroundWindowIsFullscreen
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _config.Advanced.DisableEasyMouseWhenForegroundWindowIsFullscreen;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _config.Advanced.DisableEasyMouseWhenForegroundWindowIsFullscreen = value;
                }
            }
        }

        internal HashSet<string> EasyMouseFullscreenSwitchBlockExcludedApps
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return new HashSet<string>(_config.Advanced.EasyMouseFullscreenExcludedApps, StringComparer.OrdinalIgnoreCase);
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _config.Advanced.EasyMouseFullscreenExcludedApps = value?.ToList() ?? new List<string>();
                }
            }
        }

        internal string Enc(string st, bool dec, DataProtectionScope protectionScope)
        {
            if (st == null || st.Length < 1)
            {
                return string.Empty;
            }

            byte[] ep = Common.GetBytesU(st);
            byte[] rv, st2;

            if (dec)
            {
                st2 = Convert.FromBase64String(st);
                rv = ProtectedData.Unprotect(st2, ep, protectionScope);
                return Common.GetStringU(rv);
            }
            else
            {
                st2 = Common.GetBytesU(st);
                rv = ProtectedData.Protect(st2, ep, protectionScope);
                return Convert.ToBase64String(rv);
            }
        }

        internal string PairingKeyDisplayText
        {
            get
            {
                if (IsProtectedMode)
                {
                    return Encryption.MyKey ?? string.Empty;
                }

                lock (_loadingSettingsLock)
                {
                    return _config.SecurityKey;
                }
            }
        }

        // MwbLite always uses the pass-through codec (see Setting.CreatePairingKeyCodec), so a stored key
        // can only "mismatch" if someone pasted an upstream DPAPI-protected value into settings.json.
        internal bool IsPairingKeyModeMismatch
        {
            get
            {
                lock (_pairingKeyLock)
                {
                    var storedKey = _config.SecurityKey;
                    return !string.IsNullOrEmpty(storedKey) &&
                        (_pairingKeyCodec is DpapiPairingKeyCodec) != PairingKeyCodec.IsEncryptedValue(storedKey);
                }
            }
        }

        internal string MyKey
        {
            get
            {
                lock (_pairingKeyLock)
                {
                    var key = _pairingKeyCodec.Decrypt(_config.SecurityKey);
                    if (PairingKeyCodec.IsEncryptedValue(key))
                    {
                        throw new CryptographicException("The saved pairing key cannot be used in the current mode.");
                    }

                    return key;
                }
            }

            set
            {
                lock (_pairingKeyLock)
                {
                    if (!string.Equals(_config.SecurityKey, value, StringComparison.Ordinal))
                    {
                        // Runtime change only (the upstream key-generation UI is not reachable in MwbLite).
                        _config.SecurityKey = _pairingKeyCodec.Encrypt(value);
                        LiteLog.Write(nameof(Settings), "Security key changed at runtime; settings.json is unchanged.");
                    }
                }
            }
        }

        internal bool UpdateRuntimeKey()
        {
            string key;
            bool keyCorrupted = false;
            try
            {
                key = MyKey;
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
            {
                key = string.Empty;
                keyCorrupted = !IsPairingKeyModeMismatch;
            }

            Encryption.KeyCorrupted = keyCorrupted;
            if (Encryption.MyKey == key)
            {
                return false;
            }

            Encryption.MyKey = key;
            Encryption.MagicNumber = string.IsNullOrEmpty(key) ? 0 : Encryption.Get24BitHash(key);
            return true;
        }

        internal int MyKeyDaysToExpire
        {
            get
            {
                return int.MaxValue; // TODO(@yuyoyuppe): do we still need expiration mechanics now?
            }
        }

        // Note(@htcfreek): Settings UI CheckBox is disabled in frmMatrix.cs > FrmMatrix_Load()
        internal bool DisableCAD
        {
            get
            {
                return false;
            }
        }

        // Note(@htcfreek): Settings UI CheckBox is disabled in frmMatrix.cs > FrmMatrix_Load()
        internal bool HideLogonLogo
        {
            get
            {
                return false;
            }
        }

        internal bool HideMouse
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _config.Advanced.HideMouseAtScreenEdge;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _config.Advanced.HideMouseAtScreenEdge = value;
                }
            }
        }

        internal bool BlockScreenSaver
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _config.Advanced.BlockScreenSaverOnOtherMachines;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _config.Advanced.BlockScreenSaverOnOtherMachines = value;
                }
            }
        }

        internal bool BlockScreenSaverIsGpoConfigured => false;

        internal bool MoveMouseRelatively
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _config.Advanced.MoveMouseRelatively;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _config.Advanced.MoveMouseRelatively = value;
                }
            }
        }

        internal string LastPersonalizeLogonScr
        {
            get
            {
                return string.Empty;
            }
        }

        internal uint DesMachineID
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return (uint)_state.MachineId;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _state.MachineId = (int)value;
                }
            }
        }

        internal int LastX
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _state.LastX;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    Common.LastX = value;
                    _state.LastX = value;
                }
            }
        }

        internal int LastY
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _state.LastY;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    Common.LastY = value;
                    _state.LastY = value;
                }
            }
        }

        internal int PackageID
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _state.PackageId;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _state.PackageId = value;
                }
            }
        }

        // MwbLite: pairing is done through settings.json, so the upstream first-run setup wizard never runs.
        internal bool FirstRun
        {
            get => false;
            set
            {
            }
        }

        internal int HotKeySwitchMachine
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _config.SwitchMachineHotkeyCode();
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _config.Advanced.Hotkeys.SwitchMachine = value == (int)VK.F1 ? "F1" : value == '1' ? "1" : "disabled";
                }
            }
        }

        internal HotkeySettings HotKeySwitch2AllPC
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _config.Hotkey(_config.Advanced.Hotkeys.SwitchToAllPcs);
                }
            }
        }

        internal HotkeySettings HotKeyToggleEasyMouse
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _config.Hotkey(_config.Advanced.Hotkeys.ToggleEasyMouse);
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _config.Advanced.Hotkeys.ToggleEasyMouse = value?.ToString() ?? string.Empty;
                }
            }
        }

        internal HotkeySettings HotKeyLockMachine
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _config.Hotkey(_config.Advanced.Hotkeys.LockMachine);
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _config.Advanced.Hotkeys.LockMachine = value?.ToString() ?? string.Empty;
                }
            }
        }

        internal HotkeySettings HotKeyReconnect
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _config.Hotkey(_config.Advanced.Hotkeys.Reconnect);
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _config.Advanced.Hotkeys.Reconnect = value?.ToString() ?? string.Empty;
                }
            }
        }

        internal int HotKeyExitMM
        {
            get
            {
                return 0;
            }
        }

        private int switchCount;

        internal int SwitchCount
        {
            get
            {
                return switchCount;
            }

            set
            {
                switchCount = value;
                if (!PauseInstantSaving)
                {
                    SaveSettings();
                }
            }
        }

        internal int DumpObjectsLevel => 6;

        internal int TcpPort
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _config.Advanced.TcpPort;
                }
            }
        }

        internal bool DrawMouse
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _config.Advanced.DrawMouseCursor;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _config.Advanced.DrawMouseCursor = value;
                }
            }
        }

        // Upstream default (true); was not exposed in the PowerToys Settings UI either.
        internal bool DrawMouseEx { get; set; } = true;

        internal bool ReverseLookup
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _config.Advanced.ValidateRemoteIp;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _config.Advanced.ValidateRemoteIp = value;
                }
            }
        }

        internal bool ReverseLookupIsGpoConfigured => false;

        internal bool SameSubNetOnly
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _config.Advanced.SameSubnetOnly;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _config.Advanced.SameSubnetOnly = value;
                }
            }
        }

        internal bool SameSubNetOnlyIsGpoConfigured => false;

        internal string Name2IP
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _config.Name2IpString();
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _config.Advanced.Name2Ip = (value ?? string.Empty).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).ToList();
                }
            }
        }

        internal bool Name2IpIsGpoConfigured => false;

        // MwbLite: policy-defined name/IP rules came from Group Policy; there are none.
        internal string Name2IpPolicyList => string.Empty;

        internal bool Name2IpPolicyListIsGpoConfigured => false;

        internal bool FirstCtrlShiftS
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _state.FirstCtrlShiftS;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _state.FirstCtrlShiftS = value;
                }
            }
        }

        // Was a value read from registry on original Mouse Without Border, but default should be true. We wrongly released it as false, so we're forcing true here.
        // This value wasn't changeable from UI, anyway.
        internal bool StealFocusWhenSwitchingMachine => true;

        // Upstream regenerated this per process (the stored value was always overwritten); same here.
        private readonly string deviceId = Guid.NewGuid().ToString();

        internal string DeviceId => deviceId;

        private int? machineId;

        internal int MachineId
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    machineId ??= _state.MachineId;

                    if (machineId == 0)
                    {
                        var newMachineId = Encryption.Ran.Next();
                        _state.MachineId = newMachineId;
                        machineId = newMachineId;
                        if (!PauseInstantSaving)
                        {
                            SaveSettings();
                        }
                    }
                }

                return machineId.Value;
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _state.MachineId = value;
                    machineId = value;
                    if (!PauseInstantSaving)
                    {
                        SaveSettings();
                    }
                }
            }
        }

        internal bool OneWayControlMode => false;

        internal bool OneWayClipboardMode => false;

        internal bool ShowClipNetStatus
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _config.Advanced.ShowClipboardAndNetworkStatusMessages;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _config.Advanced.ShowClipboardAndNetworkStatusMessages = value;
                }
            }
        }

        // MwbLite: the tray icon is the only UI, so it is always shown.
        internal bool ShowOriginalUI
        {
            get => true;
            set
            {
            }
        }

        // If starting the service fails, work in not service mode.
        internal bool UseService
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _config.UseService;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _config.UseService = value;
                }
            }
        }

        internal bool AllowServiceModeIsGpoConfigured => false;

        // Note(@htcfreek): Settings UI CheckBox is disabled in frmMatrix.cs > FrmMatrix_Load()
        internal bool SendErrorLogV2
        {
            get
            {
                return false;
            }
        }
    }

    public static class Setting
    {
        internal static Settings Values = new Settings(CreatePairingKeyCodec()) { RuntimeLayoutChanged = LiteLayoutSync.OnRuntimeLayoutChange };

        // MwbLite: the key is stored as plain text in every mode. Upstream's DPAPI codec protected the
        // SYSTEM instance's key inside a user-writable file; MwbLite instead keeps the SYSTEM instance's
        // settings in %ProgramData%\MwbLite, which only SYSTEM and Administrators can read or write.
        private static IPairingKeyCodec CreatePairingKeyCodec()
        {
            return new PassthroughPairingKeyCodec();
        }
    }
}
