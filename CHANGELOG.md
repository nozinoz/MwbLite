# Changelog

Each version's section is also its GitHub Release notes (see `tools/publish-release.ps1`), and Settings > About
shows them for the latest release. Upgrade every PC to the same version.

## 0.9.1

- **Install updates automatically now really keeps MwbLite up to date, silently.** With the switch on, MwbLite asks
  GitHub every 24 hours instead of weekly (and right away when you turn it on), even if "Check for updates every week"
  is off. A new version is installed in the background once nobody has used the PC for 3 minutes, so it never
  interrupts you, and there are no notifications. An install that fails is retried a day later, and you are told once
  that the update is available.

## 0.9.0

**Upgrade every PC to 0.9.0.** The new features below use a new connection between MwbLite PCs (TCP port
`tcpPort+3`, default 15103), so both PCs need 0.9.0 for them. Mouse, keyboard, clipboard and files are unchanged.

- **Stronger connection handshake: 0.9.0 can't connect to 0.8.x or earlier.** The key proof now makes the connecting
  PC prove first, and a PC without the key learns nothing it could use to guess the key offline; the key is also
  stretched with PBKDF2. Both PCs show the other as "Older version" until you update it, so update every PC.
- **Wake a sleeping PC from the tray.** A PC that isn't connected gets a **Wake LAPTOP** item (with its own name)
  in the tray menu. It needs a wired network and "Wake on Magic Packet" turned on in that PC's BIOS and network
  adapter settings; Wi-Fi laptops usually can't be woken. The PC must have been connected to this one once, so its
  network card address is known.
- **Lock all PCs** and **Sleep all PCs…** (asks first) in the tray menu. Both include this PC.
- **Line up screens of different sizes** (Settings > PCs). Drag a screen along its neighbour's edge so the cursor
  comes out where you expect, or press **Use real sizes** to scale both screens by their physical size. Apply
  sends it to the connected PCs.
- **Connection notices** (on by default; Settings > Connection): a notification when a PC that was connected has
  been gone for 20 seconds, and when it is back.
- **Save diagnostics…** (Settings > About) puts the logs, status, network details and your settings, without the
  security key, into one zip to attach to a problem report.
- **Updates without a UAC prompt.** Setup now adds an on-demand **MwbLite Update** scheduled task, and **Update
  now** uses it: it installs the new version and restarts MwbLite, with no administrator prompt. Copies that weren't
  installed with Setup still ask for UAC.
- **Install updates automatically** (off by default; Settings > About): the weekly check installs a new version by
  itself.
- **Update together with my other PCs** (on by default): when a connected PC runs an older MwbLite, this one tells
  it, and it updates itself. The other PC never downloads anything from the sender; it installs the latest GitHub
  release only.
- **Signed releases.** Updates are installed only if the release's `SHA256SUMS.txt` is signed with MwbLite's release
  key (the new `SHA256SUMS.txt.sig`), so a release published from a hijacked GitHub account isn't installed by any
  update path. Without a good signature MwbLite says "This release isn't signed with MwbLite's release key; not
  installing it." and the weekly check says the release can't be installed automatically.
- **Smaller portable exes**: about 74 MB each instead of 178 MB, at the cost of more memory and a slightly slower
  start. The installed version is unchanged.
- The new update features take effect from the release after 0.9.0. Installing 0.9.0 itself still uses the old
  update flow (one UAC prompt, no task yet).
- `tcpPort` can be at most 65532 now (it was 65534), because the new connection uses `tcpPort+3`.

## 0.8.4

- Settings buttons now work like a classic Windows dialog: **Apply and close** (like OK) saves your changes and
  closes, **Cancel** discards them, and **Apply** saves and keeps the window open.

## 0.8.3

- Settings has **Apply**, **Close** and **Cancel**: Apply saves and applies your changes and keeps the window open,
  Close asks about changes you haven't applied, Cancel discards them.

## 0.8.2

- **Same mouse speed on every PC is now on by default** for new installs: MwbLite sends the mouse's movement, so
  each PC moves its pointer at its own speed instead of feeling slow on a lower-resolution screen. Existing
  settings keep their value; turn it on under Settings > Advanced.

## 0.8.1

- **Reconnects after sleep without re-pairing.** MwbLite used to try once on waking and then stop; if the other
  PC was still asleep, or names didn't resolve yet, they never found each other until the settings changed. It now
  keeps redialling every PC that isn't connected (every 15 seconds at first, then every minute), also from the
  lock screen.
- **Remembers where each PC was last reached** and tries those addresses when its name doesn't resolve, which is
  common in the first minutes after waking.
- Settings > Advanced explains **Same mouse speed on every PC (move the mouse relatively)**: turn it on if the
  cursor feels slow on a PC whose screen resolution differs from this one's.

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
