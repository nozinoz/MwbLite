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
| 2.1 | On **A**, open **Settings**, tick **Start with Windows**, press **Save & apply**. | Balloon: "Configuration reloaded." | |
| 2.2 | On **A**: Settings > **Add a PC…** | A window says it is waiting for a PC to join. | |
| 2.3 | On **B**: Settings > **Join…** | Within a few seconds **A**'s name appears in the list. (If not, type A's computer name in the box below the list.) | |
| 2.4 | On **B**, select A and press **Join**. | Both windows show the **same** 6-digit code. A's window says "B wants to join". | |
| 2.5 | Press **Codes match** on **A**, then on **B**. | Both windows report success; B's Settings now shows A's key (Show advanced settings > Security key > Show, to compare) and the list `A, B`. A's list shows `A, B`. | |
| 2.6 | Within about 10 s, right-click the tray icon on A. | The menu lists both machine names under Exit (the connected machines). | |
| 2.7 | **Cancel test:** repeat 2.2–2.4 and press **Cancel** on B at the code step. | A's window says pairing was cancelled on the other PC; nothing changes on either PC. | |
| 2.8 | **Different-code test (optional):** start Add a PC on A and Join from B, but press Codes match on only one PC and Cancel on the other. | No settings change on either PC. | |
| 2.9 | If a Windows Firewall prompt appeared on either machine, allow **Private** networks. (With Setup, the rules are added automatically.) | — | |
| 2.10 | **Wrong key:** in Settings on B, open Show advanced settings, change one character of the security key and save. | A and B stop working together (B can't be reached from A). The logs show connection or handshake errors. | |
| 2.11 | Pair again with a code (2.2–2.5). Also try typing a 10-character key in Settings and saving. | They reconnect. The short key is refused with a red message. | |
| 2.12 | **Arrange:** on A, drag B's tile onto A's tile under Machines and layout (order becomes `B, A`) and press Save & apply. Then exit and restart MwbLite on B. | B's Settings shows the new order, both before and after the restart. | |
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
| 7.1 | In Settings, tick **Service mode** and **Start with Windows**, press **Save & apply**, and approve the UAC prompt. Then Exit MwbLite from the tray and start it again from the Start menu. | No errors. | |
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

## 9. Uninstall (optional, if you want to verify clean removal)

| # | Step | Expected | Result |
|---|---|---|---|
| 9.1 | Settings > Apps > Installed apps > MwbLite > Uninstall, approve UAC. | The service, firewall rule, `C:\Program Files\MwbLite`, the Start menu shortcuts, `%ProgramData%\MwbLite` and the start-with-Windows entry are removed. `%LOCALAPPDATA%\MwbLite` stays. | |

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
