# Widget UX states

The states the widget moves through, and what each one is allowed to show. Written during the Phase 3
integration spike because the spike's job is to prove the *platform* behaviours these states depend on
(borderless chrome, drag, topmost, tray, one instance) before any visual work is built on them.

Anything marked **spike-verified** was observed by running the app on real Windows 11; see
[Integration spike evidence](#integration-spike-evidence).

## Session states

| State | What it means | What the widget shows |
|---|---|---|
| **Stopped** | The session exists but no entry is open. | The last task, or `No task running`. Play is available. |
| **Running** | One entry is open. | The task and the timer, per the display mode. Stop and Play (stop-and-start) are available. |
| **Hidden while running** | The window is hidden to the tray and time is still being tracked. | Nothing on screen; the tray icon shows a running indicator, and the first reveal shows a non-blocking reminder that timing continues. Hiding never ends a session. |
| **Ended** | `EndedAt` is set. | The session is finished; the summary is the useful view. |

The state comes from `SessionState.IsActive` / `ActiveEntry`, never from the UI. Invariant: **the timer is
derived from the active entry's persisted start time**, not from an accumulated tick, so a hidden window, a
suspended process or a restart cannot drift the elapsed time.

## Panel states

| State | Notes |
|---|---|
| **Widget only** | The default. Compact, always on top (unless the user turned that off). |
| **Panel open (right-attached)** | Summary, Task Groups, Entries, or session/menu/preferences. One at a time. |
| **Detached windows** | Summary and Task Groups can detach. Gated behind this spike. |

## Close behaviour

| Session state | What close does |
|---|---|
| **Running** | Always prompts: *Hide to tray* or *Exit*. Never silently exits, and never offers Stop-and-hide. Choosing Exit runs end-session → forced flush → exit. |
| **Stopped** | Immediately follows the stored preference: `HideToTray` or `Exit`. |

## Preference states that affect the surface

- **Theme**: System / Light / Dark, plus an optional custom accent that must not repurpose the semantic
  green/red/amber/muted-grey meanings.
- **Timer display**: elapsed duration, or the entry's start time (`Started h:mm tt`).
- **Size preset**: Compact / Comfortable / Expanded, each with explicit minimums.
- **Always on top**: on by default; toggled from the widget and the tray menu.
- **Placement**: position plus monitor identity, with a visibly inset primary-monitor fallback when the
  recorded monitor is gone or the coordinates are off-screen.

## Integration spike evidence

Run on Windows 11 (10.0.26200, 25H2) in the `timetracker-win11` VM, unpackaged.

| # | Check | Result | Evidence |
|---|---|---|---|
| 1 | Borderless window with rounded presentation, draggable from a non-interactive surface | **pass** | Window rendered with no system border or title bar, and dragged from (149,302) to (424,527) in the VM. **The first implementation failed** — see the drag note below. |
| 2 | Text box and button keep normal interaction and do not begin a drag | **pass** | Typing `abc123` into the text box landed as `abc123`, and clicking the button toggled topmost. The window's title stayed at x=149 y=302 throughout, so neither press became a drag. |
| 3 | Topmost get/set agree | **pass** | Clicking the toggle reported `Topmost requested: False; window reports: False`, and clicking again reported `True; True`. |
| 4 | Tray icon: left-click show/focus, right-click menu callback | **partial** | The icon was created successfully — `TrayIconService.Show` throws when `Shell_NotifyIcon` fails and reports it on the surface, and the surface showed no error. The click callbacks have **not** been exercised. |
| 5 | Second launch signals the first instance to show/focus, then exits | **pass** | A second launch in the same session left exactly one `TimeTrackerDesktop.exe` running (PID 6472, session 1). |

### Drag: a measured constraint, and the fix

The first drag implementation used the classic `ReleaseCapture()` + `SendMessage(WM_NCLBUTTONDOWN, HTCAPTION)`.
**It does nothing on this window**, and the reason is structural rather than a coding slip: `HTCAPTION` names a
*non-client caption area*, and `OverlappedPresenter.SetBorderAndTitleBar(false, false)` removes exactly that.
Measured, not assumed — the drag was performed and the window did not move.

The fix keeps the borderless design (the plan forbids substituting conventional chrome) and performs the drag
by hand: capture the pointer, record the cursor's screen position and the window's position, then move the
window with `AppWindow.Move` as the pointer moves. Re-measured: the window moved as intended.

**Gate:** detached windows and visual polish do not start until all five read *pass*. Check 4's callbacks are
the outstanding item.
