# Changelog

Each version's section is also its GitHub Release notes (see `tools/publish-release.ps1`), and Settings > About
shows them for the latest release. Upgrade every PC to the same version.

## 0.8.0

- **About page in Settings.** Version and build details, links to the project, release notes and licences, and an
  update check with **Update now**, which downloads the new Setup from GitHub, checks its SHA-256 and installs it.
- **Weekly update check** (on by default, switch it off under About). When a new version is out, a notification
  says so; clicking it opens About.
- **Setup shows the version prominently:** on the banner, in the window title and in the headings.
- Version info includes the git commit the build came from.

## 0.7.0

- **Windows 11 look.** Settings and pairing are rebuilt with WPF's Fluent theme: sidebar pages, cards, switches,
  light and dark mode following Windows, live PC status on the layout tiles, Check connection on the Connection
  page.
- **New tray icon:** two screens in the taskbar's colour with a status dot (green: all PCs connected, amber:
  connecting, red: a PC is missing).
- **New Setup design** with a banner, a licence link and a "Start MwbLite when I sign in" box.
- Restyled copy-progress window; new app icon.
- Addresses show as `192.168.0.10` instead of `::ffff:192.168.0.10`.

## 0.6.0

- **Secure connection:** every connection between PCs is TLS 1.3 with a security-key proof bound to both PCs'
  certificates. MWB's own protocol runs unchanged inside it.
- PCs with a different key, or on an older version, are refused and shown as such. Allow older versions under
  Advanced for a changeover.
- Check connection tests a real secure connection to each PC.

## 0.5.0

- **Smaller:** Setup is about 8 MB; it installs Microsoft's .NET Desktop Runtime when a PC needs it.
- **Reconnects** after sleep, resume and network changes; a vanished PC is noticed in about 20 seconds.
- **Status** in the tray: each PC's state in the menu and tooltip.
- **Copy progress window** with Cancel, and a notification when a copy finishes.
- **Check connection** in Settings, with Fix firewall.
- The security key is **encrypted at rest** (Windows DPAPI).

## 0.4.2

- Upgrading over a running copy no longer stops at a "files in use" prompt.

## 0.4.1

- The security key moved under Advanced; pairing is the main way to set PCs up.

## 0.4.0

- Copy and drag **folders and several files** at once.

## 0.3.0

- Drag-and-drop **layout editor** in Settings.

## 0.2.0

- **Pair PCs with a code** (Add a PC / Join), layout kept in sync between PCs.
- MwbLite-Setup.exe installer, Settings window.

## 0.1.0

- Mouse Without Borders carved out of PowerToys as a standalone tray app with a local settings file.
