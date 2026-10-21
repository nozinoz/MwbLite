# MwbLite: standalone Mouse Without Borders

MwbLite is [Mouse Without Borders](https://learn.microsoft.com/windows/powertoys/mouse-without-borders) (MWB) carved
out of Microsoft PowerToys so it runs on its own: one tray app plus a small helper, configured by a JSON file.
It has no PowerToys runner, Settings app, updater, telemetry or Group Policy layer.

It is built from PowerToys commit `cf45de41` (see [UPSTREAM.md](UPSTREAM.md)). The networking, encryption,
input-injection and clipboard code is upstream's, unchanged apart from the lines listed in
[Changes to upstream code](#changes-to-upstream-code).

> MIT licensed, like PowerToys. [LICENSE](LICENSE) and [NOTICE.md](NOTICE.md) are upstream's; the original
> copyright headers are kept in every source file.

## Build

Requirements: Windows 10/11 x64, PowerShell 5.1 (built in), internet access for the first build. No Visual
Studio, C++ tools or system-wide .NET SDK are needed.

```bat
build.cmd
```

That single command (a wrapper for `powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1`) does three things:

1. **Installs the SDK.** It downloads the .NET SDK pinned in `global.json` into `.dotnet\` (portable). The
   Microsoft-signed `dotnet-install.ps1` is checked for a valid signature before it runs.
2. **Runs the unit tests.** These are upstream's encryption, key-codec, logger and received-file tests plus the
   config-loader tests. Pass `-SkipTests` to skip them.
3. **Publishes `dist\`.** The output is two self-contained, single-file x64 executables plus licence files and
   `SHA256SUMS.txt`.

| File | Size | Role |
|---|---|---|
| `MwbLite.exe` | ~177 MB | Tray app and MWB engine. The same exe is the Windows service (`--service`) and the installer (`--install` and related commands). |
| `MwbLite.Helper.exe` | ~176 MB | Runs as the logged-in user and handles clipboard monitoring, dragging files out, and screen capture. `MwbLite.exe` starts and stops it. |

The build keeps everything inside the repo folder:

- NuGet packages go to `.nuget\` and come from nuget.org only.
- The SDK's CLI state goes to `.dotnet\home`.
- .NET CLI telemetry is switched off.

The exes are large because each one carries its own .NET runtime. They are deliberately not compressed: a
compressed single-file exe expands into private memory at startup and used about 3× the RAM. As built, MwbLite
uses about 27 MB + 15 MB of private memory, compared with about 32 MB + 17 MB for PowerToys' MWB.

## Install and run

Copy the whole `dist\` folder to each PC. **Don't run PowerToys' Mouse Without Borders on the same PC** (turn it
off in PowerToys): both use TCP ports 15100/15101.

### Option A: normal mode (no admin)

Run `MwbLite.exe`. On first run it creates `%LOCALAPPDATA%\MwbLite\settings.json` with a random security key and
shows a balloon. Then left-click the tray icon to open **Settings** (see [Settings window](#settings-window)):

1. On the first PC, press **Copy** next to the security key. On every other PC, paste that key.
2. Enter the same computer names, in screen order, on every PC. **Add this PC** fills in the current computer.
3. Press **Save & apply**. The change takes effect immediately.

Windows Firewall will ask on first run whether to allow MwbLite. Allow **private networks**.

### Option B: installed, with lock screen and UAC prompt support (recommended)

1. In Settings, tick **Service mode** and **Start with Windows**, then press **Save & apply**. MwbLite offers the
   one-time install. Alternatively, set `"useService": true` and `"startWithWindows": true` and run
   `MwbLite.exe --install`. Approve the UAC prompt. The install:
   - copies both exes to `C:\Program Files\MwbLite\`;
   - registers the **MwbLite.Service** Windows service (demand start). It uses the same security descriptor as
     PowerToys, so you can start it without admin rights;
   - adds a firewall rule: inbound TCP on `tcpPort` and `tcpPort+1`, for `MwbLite.exe` only, from the
     **local subnet** only;
   - copies your settings.json to `%ProgramData%\MwbLite\settings.json`, a folder only SYSTEM and Administrators
     can read (see [Security notes](#security-notes));
   - sets the Run key if `startWithWindows` is true, then starts the installed copy.
3. At logon, the Run entry starts `MwbLite.exe`. Because `useService` is true, that instance starts the service
   and exits. The service then launches MwbLite as SYSTEM on the secure desktop and on your desktop (exactly as
   PowerToys does) and stops itself.
4. After editing settings.json in service mode, use **Reload config** (or run `MwbLite.exe --apply-config`). It
   asks for UAC approval, copies the file to `%ProgramData%`, and the running instances reload it.

To remove everything `--install` added, run `MwbLite.exe --uninstall`. Your `%LOCALAPPDATA%\MwbLite` settings
are kept.

### Commands

| Command | Admin | What it does |
|---|---|---|
| `MwbLite.exe` | no | Run as a tray app. With `useService: true`, it starts the service and exits. |
| `MwbLite.exe --install` | UAC | Install or update (see above). Safe to re-run. |
| `MwbLite.exe --apply-config` | UAC | Copy your settings.json to the service's protected copy. |
| `MwbLite.exe --uninstall` | UAC | Remove the service, firewall rule, Program Files copy, `%ProgramData%\MwbLite` and the Run entry. |
| `MwbLite.exe --settings` | no | Open the Settings window. |
| `MwbLite.exe --open-config` | no | Open your settings.json in Notepad. |
| `MwbLite.exe --service` | — | Service entry point. The Service Control Manager uses this; don't run it by hand. |

### Tray menu

- **Settings…** Also what a left-click on the icon does. See [Settings window](#settings-window).
- **Open config file.** Opens settings.json in Notepad.
- **Reload config.** Re-reads settings.json after you edit it by hand.
- **Exit.**
- **Machines.** Below Exit, upstream's list of connected machines (click one to switch to it).

### Settings window

![MwbLite Settings](docs/settings-window.png)

Left-click the tray icon, or choose **Settings…**, to edit settings.json without touching JSON.

- **Pairing.** Shows this PC's name. The security key has **Show**, **Generate** (a new random key) and
  **Copy** buttons.
- **Machines and layout.** Four name boxes in screen order. They regroup as a 2×2 grid when you choose **Two
  rows**.
- **Options.** Clipboard, file transfer, start with Windows, service mode.
- **Show advanced settings.** Port, Easy Mouse, hotkeys, name-to-IP mappings and the less common switches.
- **Save & apply** validates first, using the same checks as Reload config. If something is wrong, the window
  stays open and the problem is shown in red. Otherwise it writes settings.json and applies it:
  - **Normal mode:** applied immediately.
  - **Service mode:** a UAC prompt copies it to the service's protected copy.
  - **Service mode turned on but not installed yet:** the window offers the one-time install.
- **Saving rewrites settings.json.** Comments are removed (you're asked first), and the previous file is kept as
  `settings.json.bak`. Values the window doesn't show (e.g. `easyMouseFullscreenExcludedApps`) are kept.
- **Open JSON…** opens the file in Notepad for anything else.
- **It runs as you.** In service mode the tray belongs to a SYSTEM process, so the window opens as a separate
  `MwbLite.exe --settings` process with your token, never as SYSTEM.
## Configuration: `%LOCALAPPDATA%\MwbLite\settings.json`

The file is JSON. `//` comments and trailing commas are allowed. The MWB engine **never writes this file**. Only
two things do:
- MwbLite creates it when it's missing.
- The Settings window rewrites it when you press **Save & apply**, keeping the previous file as
  `settings.json.bak`.

Values MWB changes while running (machine IDs, learned machine pool, last cursor position) go to `state.json`
next to it.

```jsonc
{
  "securityKey": "<16+ characters, identical on every PC>",
  "machines": ["LAPTOP1", "MINIPC", "LAPTOP2"],   // up to 4; order = screen layout
  "layout": { "oneRow": true, "wrapMouse": true },
  "shareClipboard": true,
  "transferFile": true,
  "startWithWindows": true,
  "useService": true,
  "advanced": {
    "tcpPort": 15100,
    "hideMouseAtScreenEdge": true,
    "drawMouseCursor": true,
    "validateRemoteIp": false,
    "sameSubnetOnly": false,
    "blockScreenSaverOnOtherMachines": true,
    "moveMouseRelatively": false,
    "blockMouseAtScreenCorners": false,
    "showClipboardAndNetworkStatusMessages": false,
    "easyMouse": "enable",
    "disableEasyMouseWhenForegroundWindowIsFullscreen": true,
    "easyMouseFullscreenExcludedApps": [],
    "name2Ip": ["LAPTOP1 192.168.1.21"],
    "hotkeys": {
      "switchMachine": "F1",
      "toggleEasyMouse": "Win+Ctrl+Alt+E",
      "lockMachine": "Win+Ctrl+Alt+L",
      "reconnect": "Win+Ctrl+Alt+R",
      "switchToAllPcs": ""
    }
  }
}
```

| Key | Default | Meaning |
|---|---|---|
| `securityKey` | random | Pairing key. At least 16 characters; spaces are ignored. **Must be identical on every PC.** MWB derives its AES key from it. |
| `machines` | this PC | Up to 4 computer names (`hostname`), in screen order. **Keep this identical on every PC.** Use `""` for an empty slot. If this PC is missing, MWB puts it in the first empty slot. |
| `layout.oneRow` | `true` | `true`: machines left-to-right. `false`: 2×2 grid, row by row (`[0][1]` over `[2][3]`). |
| `layout.wrapMouse` | `true` | Leaving the last screen wraps the cursor to the first. |
| `shareClipboard` | `true` | Sync clipboard text and images. |
| `transferFile` | `true` | Copy or drag files between PCs, up to 100 MB, single file. Requires `shareClipboard`. Received files go to a `MouseWithoutBorders` folder on the desktop. |
| `startWithWindows` | `false` | Adds or removes a per-user `HKCU\…\Run` entry. **Required for service mode** to start at logon. |
| `useService` | `false` | Use MwbLite.Service, so MWB controls the lock screen, Ctrl+Alt+Del and UAC prompts. Needs `--install`. Takes effect after a restart. |
| `advanced.tcpPort` | `15100` | MWB uses `tcpPort` (clipboard and files) and `tcpPort+1` (input and control). Must match on every PC. Takes effect after a restart. |
| `advanced.validateRemoteIp` | `false` | Reverse-DNS check of connecting machines. |
| `advanced.sameSubnetOnly` | `false` | Only connect to machines on the same subnet. |
| `advanced.name2Ip` | `[]` | Static `"NAME IP"` mappings, used when name resolution is unreliable. |
| `advanced.easyMouse` | `enable` | `enable` (cross edges freely), `disable` (hotkeys only), `ctrl` or `shift` (hold the key to cross). |
| `advanced.hotkeys.*` | upstream defaults | `"Win+Ctrl+Alt+E"` style strings; `""` disables a hotkey. `switchMachine` is `"F1"` (Ctrl+Alt+F1…F4), `"1"` (Ctrl+Alt+1…4) or `"disabled"`. |
| other `advanced.*` | upstream defaults | Same meaning as the PowerToys MWB settings with the same names. |

**Reload config** validates the file first. If it's invalid, nothing changes and a balloon names the problem.
If settings.json is invalid at startup, MwbLite runs with a random key, so nothing can connect until you fix the
file and reload. `tcpPort` and `useService` changes need a restart: Exit, then start `MwbLite.exe` again.

### File locations

| Path | Contents |
|---|---|
| `%LOCALAPPDATA%\MwbLite\settings.json` | Your configuration. |
| `%LOCALAPPDATA%\MwbLite\state.json` | Runtime values (normal mode). |
| `%LOCALAPPDATA%\MwbLite\logs\` | `mwblite.log`, `helper.log`, `commands.log` (4 MB cap each, one `.1` rollover). |
| `%ProgramData%\MwbLite\` | Service mode: protected copy of settings.json, state.json, and logs (`mwblite-system.log`, `service.log`, `install.log`). SYSTEM and Administrators only. |
| `C:\Program Files\MwbLite\` | Installed binaries (`--install`). |

## What was removed or replaced

| Upstream (PowerToys) | MwbLite |
|---|---|
| PowerToys runner and C++ module interface (`ModuleInterface`, launches MWB, registers the service, firewall action, hotkeys) | `MwbLite.exe` runs by itself. Service registration and the firewall rule moved into `--install` (C#). |
| Settings pipeline: `Settings.UI.Library`, `SettingsUtils`, a PowerToys settings.json watcher, the Settings app pairing UI over named-pipe IPC (`StartSettingSyncThread`, `MouseWithoutBordersIpc`, `NamedPipePeerVerification`) | `Lite/LiteConfig.cs` reads `%LOCALAPPDATA%\MwbLite\settings.json`; `Lite/LiteState.cs` holds runtime values; the tray offers Reload. The IPC server is removed. |
| Logging via `ManagedCommon.Logger` and the Windows Event Log (`EventLogger`) | `Lite/LiteLog.cs`, a size-capped local file. |
| Telemetry: `ManagedTelemetry`, `ETWTrace`, 7 telemetry event types, 6 `WriteEvent` calls | Removed. |
| Group Policy: `GPOWrapper` (C++/WinRT), 10 policy checks | Removed. There is no policy layer. |
| "Exit when PowerToys exits" watcher (`ShutdownWithPowerToys`, `RunnerHelper`) | Removed. |
| Command Palette named events (`CommandEventHandler`, `PowerToys.Interop` constants) | Removed. The same actions remain on the MWB hotkeys. |
| Classic MWB settings form and setup wizard (opened from the tray) | Still compiled but unreachable. Every path that opened them opens settings.json instead. |
| Root build (C++ toolchain, vcpkg, Azure Artifacts feed, StyleCop, CsWinRT preview SDK package) | `Directory.Build.props` and `Directory.Packages.props` with the same package versions, from nuget.org, .NET SDK only. |
| `PowerToys.MouseWithoutBorders[Helper\|Service].exe`, service `PowerToys.MWB.Service` | `MwbLite.exe` (+ `--service`), `MwbLite.Helper.exe`, service `MwbLite.Service`. |

No PowerToys DLLs or projects are referenced. The build contains no C++.

## Changes to upstream code

**Engine files left byte-for-byte unchanged:**

- **Crypto and keys:** `Core/Encryption.cs`, `Class/PairingKeyCodec.cs`, `Class/PassthroughPairingKeyCodec.cs`, `Class/DpapiPairingKeyCodec.cs`.
- **Network and protocol:** `Class/SocketStuff.cs`, `Class/TcpServer.cs`, `Core/Receiver.cs`, `Core/Package*.cs`.
- **Input injection:** `Class/InputSimulation.cs`.
- **Process launch and native calls:** `Core/Launch.cs`, `Core/WinAPI.cs`, `Class/NativeMethods.cs`.

**Engine files with minimal, necessary edits** (`git diff baseline -- <file>` shows each one):

| File | Change | Why |
|---|---|---|
| `Class/InputHook.cs` | The module handle passed to `SetWindowsHookEx` is now `GetModuleHandle(NULL)` instead of `Marshal.GetHINSTANCE(module)`. | In a single-file exe `GetHINSTANCE` returns -1, so the keyboard and mouse hooks failed with error 126. Verified: they now install. |
| `Core/Clipboard.cs`, `Core/DragDrop.cs` | One telemetry line removed from each. | No telemetry. |
| `Core/Helper.cs`, `Helper/FormHelper.Designer.cs`, `Class/IClipboardHelper.cs` | Helper process and window renamed to `MwbLite.Helper` / "MwbLite Helper". Local clipboard pipe renamed to `MwbLite/ClipboardHelper`. `EventLogger` now writes to the log file. | These names were shared with PowerToys MWB. With both installed, MwbLite found PowerToys' helper window, and the pipe could connect to the wrong app. They are local IPC between MwbLite's own two processes, not the network protocol. |

The other edited files are the PowerToys seams: `Class/Program.cs`, `Class/Setting.cs` (same members, new backing
store), `Class/IpcChannel.cs`, `Core/Logger.cs`, `Core/MachineStuff.cs` (`ShowMachineMatrix`), `Form/frmScreen.cs`
(tray menu), `Form/frmMatrix.cs` (one telemetry line), `Helper/Program.cs`, `Service/*.cs`. New code lives in
`src/App/Lite/`.

### Stubs (fixed values that replace removed features)

| Member | Value | Note |
|---|---|---|
| `Settings.*IsGpoConfigured` (6 properties), `Name2IpPolicyListIsGpoConfigured` | `false` | Only read by the unreachable classic settings form. |
| `Settings.Name2IpPolicyList` | `""` | Policy-defined name/IP rules came from Group Policy. |
| `Settings.FirstRun` | `false` | The setup wizard is replaced by settings.json. |
| `Settings.ShowOriginalUI` | `true` | Upstream hid MWB's own tray icon when PowerToys owned the UI. The tray icon is MwbLite's only UI. |
| `Settings.DrawMouseEx` | `true` | Upstream default; it wasn't exposed in any UI. |

Upstream already hard-coded `DisableCAD`, `HideLogonLogo`, `MyKeyDaysToExpire`, `SendErrorLogV2`,
`StealFocusWhenSwitchingMachine`, `OneWay*Mode` and `HotKeyExitMM`; those are unchanged.

### Dependency changes

- **MessagePack** goes from 3.1.7 (upstream pin) to **3.1.9**, which fixes
  [GHSA-qhrr-8q5h-9q3h](https://github.com/advisories/GHSA-qhrr-8q5h-9q3h) (published 2026-10-15). It is a
  transitive dependency of StreamJsonRpc; MWB's code doesn't call it.
- All other packages use upstream's versions.

## Security notes

- **Traffic:** MWB traffic is encrypted with a key derived from `securityKey` (upstream `Encryption.cs`,
  unchanged). Anyone with the key can control your PCs. Treat settings.json like a password and use the random
  default or another long key.
- **The SYSTEM instance never reads a user-writable config.** In service mode, MWB runs as SYSTEM on the secure
  desktop and can click "Yes" on UAC prompts. If it trusted a key from a file the user (or malware running as the
  user) can edit, that would be a UAC bypass. So it reads `%ProgramData%\MwbLite\settings.json`, which is
  ACL-restricted to SYSTEM and Administrators, and changes require UAC approval (`--apply-config`).
  - **Where this differs from upstream:** PowerToys kept the service key DPAPI-encrypted inside the user's
    settings file. MwbLite keeps it as plain text in a folder only admins can access.
- **No editor or settings window runs as SYSTEM.** The SYSTEM tray's "Settings…", "Open config file" and "Reload
  config" start `MwbLite.exe --settings` / `--open-config` / `--apply-config` with **your** token.
- **Install location:** the service binary lives in `C:\Program Files\MwbLite` (admin-only write), never in a
  user-writable folder.
- **Firewall:** the rule allows only `MwbLite.exe` on the two MWB ports, from the local subnet. Upstream allowed
  the program from any address.
- **No outbound calls** other than to the MWB peers you configure: no telemetry, update checks or crash
  reporting.

## Known limitations

- **Simple settings UI only.** The Settings window replaces the classic MWB settings form and setup wizard. The
  screen-capture menu items, "About" and "Generate log" are hidden.
- **Up to 4 machines** (MWB limit). Keep `machines` and `securityKey` identical on all PCs. MWB syncs layout
  changes between connected PCs at runtime (kept in memory until Reload or restart), so differing lists cause the
  layout to flip-flop.
- **Some runtime changes are lost on restart.** These include Easy Mouse toggled by hotkey and a layout received
  from a peer, because the engine never writes settings.json.
- **Service mode starts at logon.** The service is demand-start (as in PowerToys) and is started by the Run entry
  when you log on. Lock screen and UAC control therefore work after your first logon, not at the cold-boot login
  screen.
- **Unsigned executables.** SmartScreen will warn on first run. Smart App Control (if enabled) will block them.
- **Large exes** (~177 MB each), because each carries its own .NET runtime.
- **x64 only.**
- **Not tested against stock PowerToys MWB.** The wire protocol and key derivation are unchanged, so mixed setups
  should work, but this has not been tested.
- **Updates are manual.** See below.
- **Notepad is always used** by "Open config file", not your default `.json` editor.

## Updating from upstream

```bash
git -C upstream fetch --depth 1 origin main && git -C upstream checkout FETCH_HEAD
git diff --no-index upstream/src/modules/MouseWithoutBorders/App src/App
```

1. Compare the new upstream against the `baseline` tag (the unmodified import) to see what upstream changed.
2. Port those changes onto `src/App`.
3. Pay particular attention to security fixes in `Encryption.cs`, `SocketStuff.cs`, `Receiver.cs` and
   `Clipboard.cs`.

The `upstream/` folder is a sparse clone that `.gitignore` excludes. Re-create it with:

```bash
git clone --depth 1 --filter=blob:none --sparse https://github.com/microsoft/PowerToys.git upstream
git -C upstream sparse-checkout set src/modules/MouseWithoutBorders src/settings-ui/Settings.UI.Library
```

## Testing

- **Unit tests** run as part of `build.cmd`.
- **Two-machine test:** follow [docs/TEST-PLAN.md](docs/TEST-PLAN.md).
