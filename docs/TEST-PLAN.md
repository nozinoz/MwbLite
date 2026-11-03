# MwbLite two-machine test plan

**Time:** about 30–40 minutes.

**Machines:**
- **A**: the mini PC with the keyboard and mouse.
- **B**: a laptop.
- Add the second laptop as **C** at the end, if you like.
- Sections 8c and 8d cover 0.9.0 and need **both PCs on 0.9.0**; parts of 8d need a release newer than 0.9.0.

Record each result in the **Result** column of each table (✅ pass / ❌ fail / note).

## 0. Preparation (both machines)

| # | Step | Expected |
|---|---|---|
| 0.1 | In PowerToys, turn **Mouse Without Borders off** (or quit PowerToys). | `netstat -ano \| findstr :1510` shows nothing listening. |
| 0.2 | Copy `MwbLite-Setup-<version>.exe` (from `dist\`) to each machine. Optionally compare it with `SHA256SUMS.txt` using `Get-FileHash`. | Hashes match. |
| 0.3 | Note each computer's name. In PowerShell: `hostname` | e.g. `MINIPC`, `LAPTOP1`. |
| 0.4 | Both machines must be on the same network, with that network set to **Private** (Settings > Network > Properties). | — |

## 1. First start and defaults (each machine)

| # | Step | Expected | Result |
|---|---|---|---|
| 1.1 | Run `MwbLite-Setup-<version>.exe`. If SmartScreen warns, choose "More info > Run anyway". Accept the licence, press **Install**, approve UAC, then press **Launch**. | Setup finishes without errors. The tray icon appears (it may be in the `^` overflow); a balloon says settings.json was created. **MwbLite** and **MwbLite Settings** are in the Start menu, and **MwbLite** is listed in Settings > Apps > Installed apps. | |
| 1.2 | Hover the tray icon. | Tooltip shows `[THISPC][][][]`. | |
| 1.3 | Left-click the icon (or right-click > **Settings…**). | The **MwbLite Settings** window opens. It shows this PC's name, a random (hidden) security key, and this PC in machine slot 1. Right-click > **Open config file** opens the same settings in Notepad. | |
| 1.4 | Check `%LOCALAPPDATA%\MwbLite\logs\mwblite.log`. | Contains `Keyboard/Mouse hooks installed`, `TCP listening on port: 15100` and `15101`, and `Helper process started`. | |
| 1.5 | Task Manager > Details. | `MwbLite.exe` and `MwbLite.Helper.exe` are running from `C:\Program Files\MwbLite`. No PowerToys processes are needed. | |

## 2. Pairing with a code

| # | Step | Expected | Result |
|---|---|---|---|
| 2.1 | On **A**, open **Settings**, turn on **Start with Windows** (Connection), press **Apply**. | Balloon: "Configuration reloaded." | |
| 2.2 | On **A**: Settings > **Add a PC…** | A window says it is waiting for a PC to join. | |
| 2.3 | On **B**: Settings > **Join…** | Within a few seconds **A**'s name appears in the list. (If not, type A's computer name in the box below the list.) | |
| 2.4 | On **B**, select A and press **Join**. | Both windows show the **same** 6-digit code. A's window says "B wants to join". | |
| 2.5 | Press **Codes match** on **A**, then on **B**. | Both windows report success; B's Settings now shows A's key (Advanced > Security key > Show, to compare) and the list `A, B`. A's list shows `A, B`. | |
| 2.6 | Within about 10 s, right-click the tray icon on A. | The menu lists both machine names under Exit (the connected machines). | |
| 2.7 | **Cancel test:** repeat 2.2–2.4 and press **Cancel** on B at the code step. | A's window says pairing was cancelled on the other PC; nothing changes on either PC. | |
| 2.8 | **Different-code test (optional):** start Add a PC on A and Join from B, but press Codes match on only one PC and Cancel on the other. | No settings change on either PC. | |
| 2.9 | If a Windows Firewall prompt appeared on either machine, allow **Private** networks. (With Setup, the rules are added automatically.) | — | |
| 2.10 | **Wrong key:** in Settings on B, open Advanced, change one character of the security key and save. | A and B stop working together (B can't be reached from A). The logs show connection or handshake errors. | |
| 2.11 | Pair again with a code (2.2–2.5). Also try typing a 10-character key in Settings and saving. | They reconnect. The short key is refused with a red message. | |
| 2.12 | **Arrange:** on A, drag B's tile onto A's tile on the PCs page (order becomes `B, A`) and press Apply. Then exit and restart MwbLite on B. | B's Settings shows the new order, both before and after the restart. | |
## 3. Cursor crossing the screen edge

| # | Step | Expected | Result |
|---|---|---|---|
| 3.1 | On A, move the mouse off the edge facing B (the left edge in the example). | Cursor appears on B. Keyboard input now goes to B: type into Notepad on B. | |
| 3.2 | Move back across the opposite edge of B. | Cursor returns to A. | |
| 3.3 | Press **Ctrl+Alt+F2**, then **Ctrl+Alt+F1** (positions in `machines`). | Control jumps to the 2nd machine, then back to the 1st. | |
| 3.4 | Press **Win+Ctrl+Alt+E** (toggle Easy Mouse), then try to cross the edge. | Balloon shows Easy Mouse disabled, and the edge no longer switches. Press it again to re-enable. | |
| 3.5 | In Settings > Machines, untick **Wrap the cursor around the outer edges** on both machines and save. Move off the far edge of the last machine. | Cursor no longer wraps. | |
| 3.6 | **2 × 2:** in Settings on A, choose **Two rows** and drag B into the slot **below** A; save. Move the cursor off the **bottom** of A's screen. | The cursor appears on B; leaving B's top edge returns to A. Left/right edges of A no longer lead to B. | |
| 3.7 | **Keyboard:** in Settings, click the layout, use the arrow keys to select a PC, press Ctrl+Right to move it, F2 to rename, and Esc to cancel the rename. | The tile moves and renames; Esc doesn't close Settings and Enter doesn't save it. | |

## 4. Clipboard

| # | Step | Expected | Result |
|---|---|---|---|
| 4.1 | On A, copy some text (Ctrl+C), move to B, paste (Ctrl+V). | The same text is pasted on B. | |
| 4.2 | Copy text on B, move to A, paste. | Works in the other direction too. | |
| 4.3 | Copy a screenshot (Win+Shift+S) on A and paste into Paint on B. | Image is pasted. | |
| 4.4 | **File copy:** on A, select one file (1–50 MB) in Explorer and press Ctrl+C. Move the cursor to B, click B's desktop or an Explorer folder and press **Ctrl+V**. | The file is pasted on B. | |
| 4.5 | **Drag and drop:** drag a file from A's Explorer across the edge and drop it on B's desktop. | The file arrives in `Desktop\MouseWithoutBorders` on B and that folder opens. | |
| 4.6 | In Settings on A, untick **Share the clipboard** and save. Repeat 4.1. | Text is **not** transferred. Tick it again. | |
| 4.7 | **Folder + files (copy):** on A, select a folder (with subfolders) **and** two files, Ctrl+C, move to B, Ctrl+V in an Explorer folder. | All three items are pasted, with the folder's structure intact. The balloon on B names the first item "and 2 more". | |
| 4.8 | **Folder (drag):** drag a folder from A onto B's desktop. | The folder appears inside `Desktop\MouseWithoutBorders` on B with all its contents. Drag it again: a second copy arrives as "Folder (2)". | |
| 4.9 | **Too big for copy:** copy a folder of more than 100 MB on A and move to B. | B shows "… File too big (more than 100 MB in total), please drag and drop it instead!". Dragging the same folder works. | |
| 4.10 | **Large folder (drag, optional):** drag a folder of several GB from A to B. | It arrives. If B says "still being packed", drop again a little later. | |
| 4.11 | **Progress:** drag a file of 500 MB or more from A to B. | On both PCs a small window at the bottom right shows the name, "Sending to B" / "Receiving from A", a moving bar and the speed. It doesn't take the keyboard focus. When done it shows "Sent"/"Received", closes after a few seconds, and a notification on B says it arrived. | |
| 4.12 | **Cancel:** drag the same large file again and press **Cancel** halfway (try once on A, once on B). | Both windows show the copy as cancelled or failed. No partial file is left in `Desktop\MouseWithoutBorders` on B. | |
| 4.13 | **Preparing:** drag a folder of a few GB. | The window first shows "Preparing (zipping)…" with a moving bar, then the progress. | |
| 4.14 | **Tray tooltip during a copy:** hover over the tray icon while 4.11 runs. | It shows the file name and percentage. | |

## 5. One machine sleeps

| # | Step | Expected | Result |
|---|---|---|---|
| 5.1 | With A and B connected, put **B** to sleep (Start > Power > Sleep). | A keeps working. Moving the cursor to B's edge does nothing (it stays on A). No crash; MwbLite on A stays in the tray. | |
| 5.2 | Wake B and log in. | Within about 10–60 s they reconnect, and crossing to B works again. Note the reconnect time: _____ s | |
| 5.3 | If they don't reconnect, press **Win+Ctrl+Alt+R** (Reconnect) on A. | They reconnect. | |
| 5.4 | Put **A** to sleep, wake it. | Same as 5.2, seen from B. | |
| 5.5 | Pull B's network (Wi-Fi off) for 30 s, then restore it. | They reconnect without restarting the app. | |
| 5.6 | **Tray status:** while B is asleep or off the network, hover over A's tray icon and open its menu. | The icon has an amber dot; the tooltip says "1 of 2 PCs connected" (or similar) with B "Not connected"; in the menu B shows *Not connected* and is greyed out. After B is back, the dot goes and B shows *Connected*. | |
| 5.7 | **Fast detection:** with A and B connected, pull B's network cable or turn its Wi-Fi off, and watch A's tray. | Within about 20–30 s A shows B as not connected (keepalive). Note: _____ s | |
| 5.8 | **Network change:** move B to another Wi-Fi network on the same LAN, or renew its IP (`ipconfig /renew`). | They reconnect by themselves within about 30 s. | |

## 5b. Check connection and security key

| # | Step | Expected | Result |
|---|---|---|---|
| 5b.1 | Settings > **Check connection…** on A with B connected. | Settings, MwbLite running (as a service if service mode is on), Firewall and B all show ✓. **Copy results** puts a text report on the clipboard. | |
| 5b.2 | Exit MwbLite on B, then run Check connection on A. | B shows ✗ "MwbLite isn't answering on ports…". Start MwbLite on B again. | |
| 5b.3 | On B: Windows Security > Firewall > Allow an app, untick MwbLite (or block it), then Check connection **on B**. | Firewall shows ✗ and **Fix firewall…** appears. Press it and approve UAC: the check re-runs with Firewall ✓, and A connects to B again. | |
| 5b.4 | Open `%LOCALAPPDATA%\MwbLite\settings.json` in Notepad (tray > Open config file). | `"securityKey"` reads `"dpapi:…"`, not your key. `settings.json.bak`, if present, also has no plain key. | |
| 5b.5 | In settings.json replace the `dpapi:…` value with a plain 16+ character key, save, and restart MwbLite. | It works with that key, and the file shows `dpapi:…` again. (Put the original key back afterwards, or pair again.) | |

## 5c. Secure connection (version 0.6 and later on every PC)

| # | Step | Expected | Result |
|---|---|---|---|
| 5c.1 | Install the same version on A and B. Check they connect and that mouse, keyboard, clipboard and a file copy all work. | As before; no visible lag when moving the mouse on the other PC. | |
| 5c.2 | In an admin prompt on A: `netstat -ano \| findstr 15101`, then capture a few seconds of traffic with `pktmon start --capture -f mwb.etl` / `pktmon stop` while moving the mouse on B (optional). | Connections exist on 15101; the captured payload starts with TLS records (`17 03 03` application data), not MWB's own header. | |
| 5c.3 | **Wrong key:** on B, Settings > Advanced > **Reset pairing…**, save. | Within a few seconds A's tray shows B as *Security key doesn't match* (one notification), and B shows the same for A. **Neither app quits or freezes**, and A stays connected to any third PC. Check connection on A says "its security key is different". Pair B again: they reconnect. | |
| 5c.4 | **Older version:** on B install MwbLite 0.5 (or keep one PC on 0.5 while upgrading). | A shows B as *Older version, not secure (refused)* with one notification; Check connection says B "runs an older MwbLite". | |
| 5c.5 | On A tick Advanced > **Allow older versions (not secure)**, save. | A and the 0.5 PC connect again. Untick it afterwards. | |
| 5c.6 | `certmgr.msc` (Current User > Personal > Certificates) on A. | One certificate "CN=MwbLite <PC name>" (friendly name "MwbLite secure channel"). It is recreated automatically if deleted. | |

## 6. Standalone use

| # | Step | Expected | Result |
|---|---|---|---|
| 6.1 | Carry laptop B away from A (or turn A off). Use B's own keyboard and touchpad. | B works normally; MwbLite doesn't interfere. | |
| 6.2 | While A is off, use B's keyboard and cross B's edge toward A. | Nothing happens (A is not reachable). The cursor stays on B. | |
| 6.3 | Bring B back and turn A on. | They reconnect, and you can drive from either machine's keyboard. | |

## 7. Service mode: lock screen, UAC, start with Windows (each machine)

| # | Step | Expected | Result |
|---|---|---|---|
| 7.1 | In Settings > Connection, turn on **Control the lock screen and UAC prompts** and **Start with Windows**, press **Apply**, and approve the UAC prompt. Then Exit MwbLite from the tray and start it again from the Start menu. | No errors. | |
| 7.2 | `sc qc MwbLite.Service` | `BINARY_PATH_NAME` is `"C:\Program Files\MwbLite\MwbLite.exe" --service`, `START_TYPE` is `DEMAND_START`. | |
| 7.3 | `netsh advfirewall firewall show rule name=MwbLite` | Inbound, Allow, Protocol TCP, RemoteIP LocalSubnet, Program `C:\Program Files\MwbLite\MwbLite.exe`. | |
| 7.4 | Task Manager > Details, User name column. | Two `MwbLite.exe` running as **SYSTEM**, plus `MwbLite.Helper.exe` running as you. | |
| 7.5 | Lock B (Win+L). From A, cross to B and type B's PIN or password on the lock screen. | You can unlock B from A's keyboard. | |
| 7.6 | On B, open something that triggers UAC (e.g. Services > right-click > Run as admin). Control B from A. | You can click Yes/No on B's UAC prompt from A. | |
| 7.7 | On B, open **Settings** from the tray (now owned by the SYSTEM instance), change an option (e.g. Advanced > **Show clipboard/network messages**), and save. | The window opens as you (not SYSTEM). A UAC prompt appears ("--apply-config"). After approving, the SYSTEM instances reload. | |
| 7.8 | Reboot B and log in. | MwbLite starts automatically; control from A works after logon. | |
| 7.9 | Lock and unlock several times; sleep and wake once. | Still connected each time. | |

## 8. Third machine (optional)

| # | Step | Expected | Result |
|---|---|---|---|
| 8.1 | Pair C with A: Settings > Add a PC… on A, Join… on C, confirm the code on both. | C joins; within about 10 s **B**'s Settings also lists A, B, C (B was not touched). The tray menu on A lists all three. Restart B: the list stays. | |
| 8.2 | Cross A → B → C and back. Copy and paste text from C to A. | Works. | |

## 8b. Look and feel (0.7 and later)

| # | Step | Expected | Result |
|---|---|---|---|
| 8b.1 | Run `MwbLite-Setup-<version>.exe`. | Blue banner with the MwbLite logo; Install page with a "read the licence terms" link (opens the licence), "I agree" box (Install stays disabled until ticked) and "Start MwbLite when I sign in" (ticked). Progress page, then "MwbLite is ready" with **Open MwbLite**. | |
| 8b.2 | After installing with the box ticked: `reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v MwbLite` | The entry exists, and Settings > Connection shows **Start with Windows** on. | |
| 8b.3 | Tray icon with all PCs connected, then turn one PC's Wi-Fi off, then back on. | Green dot → (about 20 s) red dot → amber while reconnecting → green. Hover text matches. | |
| 8b.4 | Windows Settings > Personalisation > Colours: switch between Light and Dark. | The tray glyph changes between black and white within about 30 s. Settings opened afterwards follows the mode. | |
| 8b.5 | Open Settings. Visit each page. | Windows 11 look: sidebar with a highlight pill, cards, switches. The PCs page shows each PC's live state on its tile and in the status list. | |
| 8b.6 | On the PCs page: arrow keys, Ctrl+arrow, F2 (type a name, Enter), Delete, drag a tile. | Selection moves, tiles move and swap, rename and remove work; nothing saves until **Apply**. | |
| 8b.7 | Change something, press **Apply**; change something else and press **Apply and close**; reopen, change again and press **Cancel**; reopen, change again and press Esc. | Apply saves and the window stays open ("Applied."). Apply and close saves and closes. Cancel closes without asking and the change is not saved. Esc asks "Apply your changes before closing?" (Yes applies, No discards, Cancel keeps the window open). | |
| 8b.8 | In normal mode (no service), change **Wrap the cursor** and press Apply. | The running MwbLite applies it within a second ("Configuration reloaded" balloon); no Reload needed. | |
| 8b.9 | Display scaling 150% (Windows Settings > Display), then open Settings and copy a large file. | Settings, the tray icon and the progress window are sharp, not blurry. | |
| 8b.10 | Run Setup. | The version is on the banner (white "VERSION x.y.z" badge), in the title bar and in the Install heading. | |
| 8b.11 | Settings > About. | Version and build, details (Copy details works), links open GitHub / the licence / notices. Within a few seconds: "You're up to date" (or the newer version and its notes). | |
| 8b.12 | **Update (without the update task, e.g. a portable copy):** with a newer release published on GitHub, open About and press **Update now**. | It downloads Setup with a progress bar, Settings closes, Setup opens (UAC), and MwbLite comes back on the new version; About then shows it. (With Setup installed, see 8d.6: no prompt.) | |
| 8b.13 | **Weekly check:** delete `update.json` next to settings.json (service mode: `%ProgramData%\MwbLite\update.json`, admin) and restart MwbLite while a newer release exists. | Within about a minute a notification says the new version is available; clicking it opens Settings > About. | |

## 8c. Version 0.9.0: control connection, Wake, lock/sleep all, screen alignment, notices, diagnostics

Needs **A** and **B** both on 0.9.0 (or later), paired and connected. Examples use `DESK-PC` for A and `LAPTOP` for B.

| # | Step | Expected | Result |
|---|---|---|---|
| 8c.1 | **Control connection:** on A, Settings > Connection > **Check now**. | Next to B's row there is an extra row "MwbLite control channel (LAPTOP)": ✓ "LAPTOP answers on port 15103". | |
| 8c.2 | `netstat -ano \| findstr :15103` on A and on B. | One MwbLite process is listening on 15103 on each PC (in service mode only the instance that owns the active desktop). | |
| 8c.3 | **Older peer (optional):** put MwbLite 0.8.4 on B and run Check connection on A. | The control channel row is a **warning**: "LAPTOP doesn't answer on port 15103. It probably runs an older MwbLite…". Mouse, keyboard and clipboard still work. **Lock all PCs** on A locks only A: B can't be asked. | |
| 8c.4 | **Wrong key:** Reset pairing on B (Advanced), then run Check connection on A. | B is "Security key doesn't match" as in 5c.3; the control channel is a warning (different key or no answer). Pair again afterwards. | |
| 8c.5 | **Firewall:** `netsh advfirewall firewall show rule name=MwbLite` on a PC installed with Setup. | The rule allows the program (all TCP ports) from LocalSubnet, so 15103 is reachable. For a portable copy after `--install`, the rule's LocalPort list includes `tcpPort+3` (15103). | |
| 8c.6 | **Other port:** set `advanced.tcpPort` to 15200 on both PCs, restart MwbLite on both. Then try 65533. | They connect and the control channel answers on 15203. Settings refuses 65533 with a red message ("between 1024 and 65532"). Set it back to 15100 afterwards. | |
| 8c.7 | **Lock all PCs:** on A, tray menu > **Lock all PCs**. | B locks first, then A. Unlock both. Repeat with B in service mode (7.1), where the lock still reaches the signed-in user. | |
| 8c.8 | **Lock all, B not connected:** turn B's Wi-Fi/cable off, wait for B to show *Not connected*, then **Lock all PCs** on A. | Only A locks. No error. | |
| 8c.9 | **Sleep all PCs:** tray menu > **Sleep all PCs…** on A, answer **No**, then again and answer **Yes**. | No: nothing happens. Yes: B goes to sleep, then A. Wake both with the keyboard or power button; they reconnect (as in 5.2). | |
| 8c.10 | **Learning addresses:** with B connected for 10 s or more, open `%LOCALAPPDATA%\MwbLite\addresses.json` on A (service mode: the one under the account that owns the tray you use). | There is an entry `"mac:LAPTOP"` with one or more `AA-BB-CC-DD-EE-FF` addresses next to the IP entries. | |
| 8c.11 | **Wake menu:** with B connected, open A's tray menu. Then disconnect B (sleep it). | While B is connected there is no *Wake* item for it. Once B shows *Not connected*, **Wake LAPTOP** appears below the PC list, above *Lock all PCs*. A PC whose address was never learned (never connected) has no Wake item. | |
| 8c.12 | **Wake from sleep (wired):** B on Ethernet, "Wake on Magic Packet" on in its adapter and BIOS. Put B to sleep, click **Wake LAPTOP** on A. | A balloon says a wake-up signal was sent. B wakes within about a minute and A shows it *Connected* without further action (A redials B every few seconds for two minutes). Note the time: _____ s | |
| 8c.13 | **Wake from shut down / hibernate (optional):** repeat 8c.12 with B shut down, if the BIOS allows. | Same, or note that it doesn't wake. | |
| 8c.14 | **Wi-Fi laptop in Modern Standby (optional):** repeat 8c.12 with a Wi-Fi-only laptop. | It usually does **not** wake. Note the result; this is the documented limitation. | |
| 8c.15 | **No Wake-on-LAN setup:** repeat 8c.12 with "Wake on Magic Packet" off in the adapter. | B stays asleep, and A shows it *Not connected* (nothing crashes; the balloon says only that a signal was sent). | |
| 8c.16 | **Line up screens, layout:** Settings > PCs > **Line up screens**, with A and B side by side. | One card for the pair, with a diagram of the two screens, **Position** and **Size** sliders, **Reset** and **Use real sizes**. With no neighbours (one PC), a hint says to put two PCs next to each other. | |
| 8c.17 | **Edit and apply** (B to the right of A): drag B's screen in the diagram so it sits on the lower half of A's right edge (Position about 0.5, Size about 0.5), press **Apply**. On A, cross to B at the very top of A's edge, at the middle, and at the very bottom. | The top and middle of A's edge both come out at the **top** of B; below the middle the position rises proportionally; the bottom of A comes out at the bottom of B. (Before: top to top, middle to middle.) | |
| 8c.18 | **Both directions:** from B, cross back to A at B's top, middle and bottom. | B's top edge comes out at the **middle** of A's edge, B's bottom edge at the bottom of A, B's middle in between. Crossing A to B and back at the same spot returns to roughly the same height. | |
| 8c.19 | **Use real sizes:** with both PCs connected, press **Use real sizes**. | After a moment the row says "Lined up to scale: DESK-PC is ... mm tall, LAPTOP ... mm. Apply to save." The span is centred and its size matches the screens' physical heights. Apply and cross to check. | |
| 8c.20 | **Use real sizes, B off:** turn B off or off the network and open the page. | The button is disabled with a reason ("LAPTOP isn't connected…"). | |
| 8c.21 | **Reset:** press **Reset**, **Apply**. | Crossing behaves as before 8c.17 (top to top, proportional). The `advanced.edgeAlignment` entry is gone from settings.json. | |
| 8c.22 | **Other PCs follow:** apply a changed alignment on A. Open Settings > PCs on B (no restart), then restart MwbLite on B and look again. Check B's settings.json. | B shows the same span both times, and its settings.json has `advanced.edgeAlignment` with the same values. Crossing from B to A uses it. | |
| 8c.23 | **Service mode receiver:** repeat 8c.22 with B in service mode. | Same; B's own (user) settings.json gets the entry, and the SYSTEM instance uses it. | |
| 8c.24 | **PC off while applying:** with B off, change the alignment on A and apply; start B. | B keeps its old alignment until you press Apply on A again. | |
| 8c.25 | **Two rows (2 × 2):** put B below A and open Line up screens. | The card describes B along A's bottom edge. Setting a span and applying moves where the cursor comes out horizontally when crossing down and back up. | |
| 8c.26 | **Layout change:** after setting a span for A-B, drag B so it is no longer A's neighbour (or remove it) and press **Apply**. | The A-B entry is dropped from settings.json. | |
| 8c.27 | **Bad value:** in settings.json set an entry's `start` and `end` equal, save, **Reload config** (or Apply the Settings window after editing). | A red message names `advanced.edgeAlignment[0]`; Settings opens on the PCs page. Nothing changes until the file is fixed. | |
| 8c.28 | **Notices on:** (default) with A and B connected, pull B's network cable or turn its Wi-Fi off. | After about 20 s a balloon on A says "LAPTOP disconnected". Restore the network: "LAPTOP is back" when it reconnects. | |
| 8c.29 | **Short drop:** turn B's Wi-Fi off for about 10 s and back on. | If B reconnects within 20 s, there is no balloon at all. | |
| 8c.30 | **Quiet period:** restart MwbLite on A with B off. Then switch B on within 2 minutes; also sleep and wake A once. | No "disconnected" or "is back" balloons for B in the first 2 minutes after the start, or after A resumes. A PC that was never connected since MwbLite started is never reported. | |
| 8c.31 | **Switch off:** untick **Tell me when a PC connects or drops off** (Settings > Connection), Apply, repeat 8c.28. | No balloons. Tray icon and tooltip still change as usual. Tick it again afterwards. | |
| 8c.32 | **Save diagnostics:** Settings > About > **Save diagnostics…**. | A Save dialog opens on the Desktop with `MwbLite-diagnostics-<PC>-<date>-<time>.zip`. After saving, Explorer opens with the file selected, and Settings says "Diagnostics saved." (or says how many items couldn't be included). | |
| 8c.33 | **Open the zip.** | It has the logs, status, settings.json, the update state, the address book, the Check connection results, version/Windows/network information, the firewall rules that mention MwbLite, and `summary.txt`. settings.json shows `"securityKey": "<redacted>"` and no `dpapi:` values; search the whole zip for your key (Advanced > Show) to be sure it isn't there. In a session that can't read `%ProgramData%\MwbLite`, `summary.txt` says which items were left out. | |
| 8c.34 | **Cancel:** press **Save diagnostics…** and cancel the dialog. | Nothing is saved; no message. | |
| 8c.35 | **Portable build:** run `dist\portable\MwbLite.exe` (about 74 MB; `MwbLite.Helper.exe` next to it) on a PC without MwbLite installed. | Starts, tray icon appears, Settings opens (a little slower and using more memory than the installed build). Check connection works. | |

## 8d. Version 0.9.0: updates without a UAC prompt, automatic updates, update together

Parts of this can only be tested once **a release newer than 0.9.0 is published** on GitHub (for example 0.9.1): 0.9.0 itself is installed with the old flow. Install 0.9.0 with Setup on A and B first. Run the task checks (8d.1 to 8d.5) right after installing.

| # | Step | Expected | Result |
|---|---|---|---|
| 8d.1 | **Task exists after install:** in an admin prompt, `schtasks /query /tn "MwbLite Update" /v /fo list`. | The task exists. Run As User: SYSTEM. Task To Run: `"C:\Program Files\MwbLite\MwbLite.exe" --install-update` (or similar). In Task Scheduler it has no triggers. | |
| 8d.2 | **Upgrade keeps it:** run a Setup of the same or a newer version over an installed copy; query the task again. | The task is still there. | |
| 8d.3 | **Standard user can start it:** as a standard (non-admin) user, `schtasks /run /tn "MwbLite Update"`. With no newer release, check `%ProgramData%\MwbLite\logs\update.log` as an admin. | The command succeeds with **no UAC prompt**. update.log shows the task started, found no newer release and stopped; nothing is installed. | |
| 8d.4 | **Standard user can't change it:** as a standard user, `schtasks /delete /tn "MwbLite Update" /f` and `schtasks /change /tn "MwbLite Update" /disable`. | Both fail with Access denied. | |
| 8d.5 | **No task without Setup:** run the portable exe, or install with `MwbLite.exe --install`, and query the task. | No task. Settings > About: **Install updates automatically** is greyed out with the explanation "Only when MwbLite was installed with Setup". | |
| 8d.6 | **Update now, no UAC:** with a newer release published, open About as a **standard user** and press **Update now**. | "Installing 0.9.x… MwbLite will restart." No UAC prompt. After a short time Settings closes, MwbLite comes back in the tray **running as you, not elevated** (Task Manager > Details), About shows the new version, and your settings are kept. In service mode, MwbLite starts the service again as at every sign-in (7.4). | |
| 8d.7 | **update.log:** as an admin, open `%ProgramData%\MwbLite\logs\update.log`. | Lines for the check, the download into `%ProgramData%\MwbLite\update`, the SHA-256 check, Setup's exit code (0) and the restart of MwbLite. | |
| 8d.8 | **Failed update (optional):** press **Update now** and disconnect the network straight away. | After a while Settings says "The update didn't finish" and points at update.log; MwbLite stays on the old version and keeps running. | |
| 8d.9 | **Uninstall removes it:** after 9.1, `schtasks /query /tn "MwbLite Update"`. | "The system cannot find the file specified": the task is gone. (An upgrade does not remove it.) | |
| 8d.10 | **Automatic updates:** on A tick **Install updates automatically** (About), Apply; delete `update.json` next to settings.json and restart MwbLite while a newer release exists. | Within about a minute a balloon says "Updating MwbLite to 0.9.x… MwbLite will restart." and MwbLite restarts on the new version, with no prompt. With the switch off (default) the balloon only says a new version is available. | |
| 8d.11 | **Automatic needs the weekly check:** untick **Check for updates every week**, restart. | No check and no automatic update happens. | |
| 8d.12 | **Update together:** A and B on 0.9.0, **Update together with my other PCs** on (default) on both. Update **only A** to the newer release (Update now). | Within about a minute of A reconnecting to B, B starts updating by itself: a balloon "Updating MwbLite to 0.9.x… (DESK-PC already runs it.)" and B restarts on the new version. Both PCs end on the same version and reconnect. | |
| 8d.13 | **Without the task:** repeat 8d.12 with B running a portable copy. | B only shows "DESK-PC runs MwbLite 0.9.x. Open Settings > About to update." | |
| 8d.14 | **Switch off:** untick **Update together with my other PCs** on B, repeat 8d.12 from the start. | B does not update by itself. Re-enable it afterwards. | |
| 8d.15 | **Signed release installs:** build a release with a real key (`tools\release-key.ps1 -New`, then `publish-release.ps1`, with a version newer than the one installed). Check the release has `SHA256SUMS.txt.sig`. Update the installed copy with **Update now**. | The update installs as in 8d.6, and update.log says "the checksum list is signed with the release key". |
| 8d.16 | **Unsigned release is refused:** publish (or edit a test release to) a version without `SHA256SUMS.txt.sig`, or delete the `.sig` asset from the release. On a PC with the older version press **Check for updates** in About. | About says the version is available but "MwbLite can't install it: This release isn't signed with MwbLite's release key; not installing it."; there is no **Update now** button. The weekly check's notification says it can't be installed automatically. With **Install updates automatically** on, nothing is installed. `schtasks /run /tn "MwbLite Update"` as an admin leaves the version unchanged and update.log has "Refusing the release". |
| 8d.17 | **Wrong signature is refused:** replace `SHA256SUMS.txt.sig` on a test release with a signature made by a different key (`release-key.ps1 -New -KeyPath <temp>` then `-Sign`, which refuses unless you edit LiteReleaseKey.cs; or just alter one character of the .sig). Repeat 8d.16. | Same as 8d.16. |
| 8d.18 | **winget (not yet submitted):** follow `packaging/winget/README.md` to make the manifest for the release (`tools\make-winget-manifest.ps1`). | Three files in `out\winget\<version>\`, and `winget validate` passes. | |

## 9. Uninstall (optional, if you want to verify clean removal)

| # | Step | Expected | Result |
|---|---|---|---|
| 9.1 | Settings > Apps > Installed apps > MwbLite > Uninstall, approve UAC. | The service, firewall rule, the "MwbLite Update" scheduled task, `C:\Program Files\MwbLite`, the Start menu shortcuts, `%ProgramData%\MwbLite` and the start-with-Windows entry are removed. `%LOCALAPPDATA%\MwbLite` stays. | |

## Troubleshooting

- **Logs:**
  - Normal mode: `%LOCALAPPDATA%\MwbLite\logs\`.
  - Service mode: `%ProgramData%\MwbLite\logs\` (open an admin prompt to read them).
- **No connection:** run Settings > **Check connection…** on both PCs first. Then:
  - Check the firewall rule or prompt, and that the network profile is Private.
  - Check `tcpPort` is the same on all machines.
  - Check names in `machines` match `hostname` exactly.
  - If name resolution fails, add `advanced.name2Ip` entries, e.g. `"LAPTOP1 192.168.1.21"`.
- **"settings.json has errors" balloon:** the message names the bad field. MwbLite runs with a random key until
  the file is fixed and reloaded.
- **Port already in use** (`10048` in the log): PowerToys MWB (or another MwbLite) is still running.
