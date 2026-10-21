# MwbLite two-machine test plan

**Time:** about 30–40 minutes.

**Machines:**
- **A**: the mini PC with the keyboard and mouse.
- **B**: a laptop.
- Add the second laptop as **C** at the end, if you like.

Record each result in the **Result** column of each table (✅ pass / ❌ fail / note).

## 0. Preparation (both machines)

| # | Step | Expected |
|---|---|---|
| 0.1 | In PowerToys, turn **Mouse Without Borders off** (or quit PowerToys). | `netstat -ano \| findstr :1510` shows nothing listening. |
| 0.2 | Copy `dist\` to each machine, e.g. `C:\Tools\MwbLite\`. Optionally compare `SHA256SUMS.txt` with `Get-FileHash`. | Hashes match. |
| 0.3 | Note each computer's name. In PowerShell: `hostname` | e.g. `MINIPC`, `LAPTOP1`. |
| 0.4 | Both machines must be on the same network, with that network set to **Private** (Settings > Network > Properties). | — |

## 1. First start and defaults (each machine)

| # | Step | Expected | Result |
|---|---|---|---|
| 1.1 | Double-click `MwbLite.exe`. If SmartScreen warns, choose "More info > Run anyway". | Tray icon appears (it may be in the `^` overflow). A balloon says settings.json was created. | |
| 1.2 | Hover the tray icon. | Tooltip shows `[THISPC][][][]`. | |
| 1.3 | Left-click the icon (or right-click > **Open config file**). | Notepad opens `%LOCALAPPDATA%\MwbLite\settings.json` with a random `securityKey`. | |
| 1.4 | Check `%LOCALAPPDATA%\MwbLite\logs\mwblite.log`. | Contains `Keyboard/Mouse hooks installed`, `TCP listening on port: 15100` and `15101`, and `Helper process started`. | |
| 1.5 | Task Manager > Details. | `MwbLite.exe` and `MwbLite.Helper.exe` are running. No PowerToys processes are needed. | |

## 2. Pairing with the security key

| # | Step | Expected | Result |
|---|---|---|---|
| 2.1 | On **A**, copy the `securityKey` value. Set `machines` to the layout as you sit, e.g. `["LAPTOP1", "MINIPC"]` (laptop to the left). Save. | — | |
| 2.2 | On **A**, right-click the tray icon > **Reload config**. | Balloon: "Configuration reloaded." | |
| 2.3 | On **B**, paste the **same** `securityKey` and the **same** `machines` list. Save, then **Reload config**. | Balloon: "Configuration reloaded." | |
| 2.4 | If a Windows Firewall prompt appeared on either machine at first start, allow **Private** networks. In normal mode without `--install`, you may need to restart MwbLite after allowing. | — | |
| 2.5 | Within about 10 s, hover the tray icon on A. Right-click it. | The menu lists both machine names under Exit (the connected machines). | |
| 2.6 | **Negative test:** on B, change one character of the key and reload. | A and B stop working together (B can't be reached from A). The logs show connection or handshake errors. | |
| 2.7 | Restore the correct key on B and reload. | They reconnect. | |

## 3. Cursor crossing the screen edge

| # | Step | Expected | Result |
|---|---|---|---|
| 3.1 | On A, move the mouse off the edge facing B (the left edge in the example). | Cursor appears on B. Keyboard input now goes to B: type into Notepad on B. | |
| 3.2 | Move back across the opposite edge of B. | Cursor returns to A. | |
| 3.3 | Press **Ctrl+Alt+F2**, then **Ctrl+Alt+F1** (positions in `machines`). | Control jumps to the 2nd machine, then back to the 1st. | |
| 3.4 | Press **Win+Ctrl+Alt+E** (toggle Easy Mouse), then try to cross the edge. | Balloon shows Easy Mouse disabled, and the edge no longer switches. Press it again to re-enable. | |
| 3.5 | Edit `layout.wrapMouse` to `false` on both machines and reload. Move off the far edge of the last machine. | Cursor no longer wraps. | |

## 4. Clipboard

| # | Step | Expected | Result |
|---|---|---|---|
| 4.1 | On A, copy some text (Ctrl+C), move to B, paste (Ctrl+V). | The same text is pasted on B. | |
| 4.2 | Copy text on B, move to A, paste. | Works in the other direction too. | |
| 4.3 | Copy a screenshot (Win+Shift+S) on A and paste into Paint on B. | Image is pasted. | |
| 4.4 | **File copy:** on A, select a file of 1–50 MB in Explorer and press Ctrl+C. Move to B, click the desktop, press Ctrl+V. | File arrives in `Desktop\MouseWithoutBorders\` on B. | |
| 4.5 | **Drag and drop:** drag a file from A's Explorer across the edge and drop it on B's desktop. | File arrives on B. | |
| 4.6 | Set `"shareClipboard": false` on A and reload. Repeat 4.1. | Text is **not** transferred. Set it back to `true`. | |

## 5. One machine sleeps

| # | Step | Expected | Result |
|---|---|---|---|
| 5.1 | With A and B connected, put **B** to sleep (Start > Power > Sleep). | A keeps working. Moving the cursor to B's edge does nothing (it stays on A). No crash; MwbLite on A stays in the tray. | |
| 5.2 | Wake B and log in. | Within about 10–60 s they reconnect, and crossing to B works again. Note the reconnect time: _____ s | |
| 5.3 | If they don't reconnect, press **Win+Ctrl+Alt+R** (Reconnect) on A. | They reconnect. | |
| 5.4 | Put **A** to sleep, wake it. | Same as 5.2, seen from B. | |
| 5.5 | Pull B's network (Wi-Fi off) for 30 s, then restore it. | They reconnect without restarting the app. | |

## 6. Standalone use

| # | Step | Expected | Result |
|---|---|---|---|
| 6.1 | Carry laptop B away from A (or turn A off). Use B's own keyboard and touchpad. | B works normally; MwbLite doesn't interfere. | |
| 6.2 | While A is off, use B's keyboard and cross B's edge toward A. | Nothing happens (A is not reachable). The cursor stays on B. | |
| 6.3 | Bring B back and turn A on. | They reconnect, and you can drive from either machine's keyboard. | |

## 7. Service mode: lock screen, UAC, start with Windows (each machine)

| # | Step | Expected | Result |
|---|---|---|---|
| 7.1 | Set `"useService": true` and `"startWithWindows": true`. Run `MwbLite.exe --install` from the dist folder and approve UAC. | Message: "MwbLite installed to C:\Program Files\MwbLite and started." | |
| 7.2 | `sc qc MwbLite.Service` | `BINARY_PATH_NAME` is `"C:\Program Files\MwbLite\MwbLite.exe" --service`. | |
| 7.3 | `netsh advfirewall firewall show rule name=MwbLite` | Inbound, TCP 15100,15101, RemoteIP LocalSubnet. | |
| 7.4 | Task Manager > Details, User name column. | Two `MwbLite.exe` running as **SYSTEM**, plus `MwbLite.Helper.exe` running as you. | |
| 7.5 | Lock B (Win+L). From A, cross to B and type B's PIN or password on the lock screen. | You can unlock B from A's keyboard. | |
| 7.6 | On B, open something that triggers UAC (e.g. Services > right-click > Run as admin). Control B from A. | You can click Yes/No on B's UAC prompt from A. | |
| 7.7 | Edit settings.json on B (e.g. `showClipboardAndNetworkStatusMessages`), then tray > **Reload config**. | A UAC prompt appears ("--apply-config"). After approving, the SYSTEM instances reload. | |
| 7.8 | Reboot B and log in. | MwbLite starts automatically; control from A works after logon. | |
| 7.9 | Lock and unlock several times; sleep and wake once. | Still connected each time. | |

## 8. Third machine (optional)

| # | Step | Expected | Result |
|---|---|---|---|
| 8.1 | Add C to `machines` on **all three** machines (same order everywhere) and reload. | Tray menu on A lists all three. | |
| 8.2 | Cross A → B → C and back. Copy and paste text from C to A. | Works. | |

## 9. Uninstall (optional, if you want to verify clean removal)

| # | Step | Expected | Result |
|---|---|---|---|
| 9.1 | `MwbLite.exe --uninstall` (from the dist folder), approve UAC. | Service, firewall rule, `C:\Program Files\MwbLite`, `%ProgramData%\MwbLite` and the Run entry are removed. `%LOCALAPPDATA%\MwbLite` stays. | |

## Troubleshooting

- **Logs:**
  - Normal mode: `%LOCALAPPDATA%\MwbLite\logs\`.
  - Service mode: `%ProgramData%\MwbLite\logs\` (open an admin prompt to read them).
- **No connection:**
  - Check the firewall rule or prompt, and that the network profile is Private.
  - Check `tcpPort` is the same on all machines.
  - Check names in `machines` match `hostname` exactly.
  - If name resolution fails, add `advanced.name2Ip` entries, e.g. `"LAPTOP1 192.168.1.21"`.
- **"settings.json has errors" balloon:** the message names the bad field. MwbLite runs with a random key until
  the file is fixed and reloaded.
- **Port already in use** (`10048` in the log): PowerToys MWB (or another MwbLite) is still running.
