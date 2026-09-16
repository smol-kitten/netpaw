# Plan: NetPaw v0.12 "main window"

## Goal
An optional main window shows everything the tray menu reaches (status, profiles, tools, map,
log) in one resizable dark window with a left navigation. The tray, the quick panel and the info
card keep working unchanged. Ships as v0.12.0.

## Given state
- `main` at v0.11.1 (PR #49): VLAN query without System.Management, Tools submenu, panel entries
  per tool. 216 Core tests.
- The Tray project is 3 042 lines in 11 files. Every view is a `Form`: `QuickPanel` (250 lines,
  borderless popup), `InfoToast` (174, the info card: `_title`, `_state`, a 2-column `_grid`
  rendered by `Render(Snapshot?)`), `NetworkMapForm` (204, four tabs), `ProfileEditorForm` (321),
  `ScanForm` (140), `SettingsForm` (267), `TraceForm`, `HelpForm`, `PlanPreviewForm`.
- Tray left click toggles the quick panel (`TrayApp.cs:87`, `TogglePanel`). `--panel` opens the
  panel at start. No double-click handler. No window remembers its size.
- `Theme` gives dark chrome to every form (`Native.Dress`, `DarkTabControl`, `DarkListView` rules).
- Actions live on `TrayApp` (`ApplyProfileVerified`, `ApplyDhcp`, `Renew`, `ResetAdapter`,
  `Reach`, `ShowMap(tab)`, `ShowTrace`, `AskCheckPort`, `AskWake`, `ExportDiagnostics`,
  `ShowScan`, `ShowEditor`, `ShowSettings`, `Advisories`, `IntentAdvisories`, `LastSnapshot`).
- `JsonStore.LogFile` / `IncidentsFile`. `Settings` has no window state.
- Operator asked for it on 2026-09-16: "maybe also make a main window to optionally open instead of
  navigating through the small tray". No standing rules for scope `netpaw`. VM 150 sleeps on disk.

## Requirements
- R1 `MainForm`: resizable, dark, 960 × 640 default, remembers size and position
  (`Settings.MainWindow`), left navigation with pages **Overview · Profiles · Tools · Map · Log**.
- R2 Overview = the info card content plus an action bar (DHCP, Renew, Reset adapter, Reach box).
  The info card and the Overview render from one control.
- R3 Profiles page: list (name, summary, adapter, *current* / *managed* badge), *Apply*, *Edit…*,
  *New…*, *Capture current…*. double-click applies. hotkey column.
- R4 Tools page: one button per tool with a one-line description. trace and port check show their
  result inside the page (no extra window). the others open their existing window.
- R5 Map page hosts the network map (same four tabs) inside the window.
- R6 Log page: last 200 lines of `netpaw.log` and the incident log, refresh, *Open folder*.
- R7 Ways in: tray menu *Open NetPaw window*, tray double-click, `--main` at start, setting
  *Tray click opens: quick panel / main window* (default: quick panel), F1 help per page.
- R8 Closing the window hides it (the tray stays). *Exit* stays in the tray menu.
- Must not: change what the quick panel, info card, tray menu or hotkeys do. block the UI thread
  (every action already runs through `RunInBackground` / `Guarded`). add dependencies. grow the
  portable build past 2 MB.

## Design details
**Panels, not forms.** Two refactors with no behaviour change: `InfoCardPanel : UserControl`
(the `_grid` + `Render(Snapshot?)` from `InfoToast`. the toast keeps title, state, pin, close and
hosts the panel) and `NetworkMapPanel : UserControl` (everything in `NetworkMapForm` below the
title bar. the form becomes a 10-line host). `MainForm` hosts both.

**Layout.** `MainForm` = `SplitContainer` fixed at 180 px: left a `ListBox` (owner-drawn nav with
icons from `Icons.Dot`), right a `Panel` that swaps one page control. Pages are `UserControl`s:
`OverviewPage`, `ProfilesPage`, `ToolsPage`, `MapPage` (wraps `NetworkMapPanel`), `LogPage`.
The window subscribes to `TrayApp.Status` and `RefreshState` to re-render the current page.

**State.** `Settings.MainWindow { int X, Y, W, H; bool Maximized; string LastPage }` and
`Settings.TrayClick = "panel" | "main"`. Saved on close. validated against the screen bounds on load.

**Tools results inline.** `ToolsPage` has a result `TextBox` (read-only, monospace): trace writes
hops as they arrive (`IProgress<Hop>`), port check writes its verdict line. Other buttons call the
existing `TrayApp` methods.

## Options considered
- A. One big tabbed window that replaces the tray: rejected: the operator wants the tray kept;
  admins use the hotkey + panel for speed.
- B. Show the existing forms as MDI children: rejected: WinForms MDI is light-themed and clunky;
  the forms would still be separate windows.
- C. Chosen: extract two panels, add a nav window that hosts them and the actions that exist.

## Chosen approach
Extract the info card and the map into `UserControl`s so the same code paints the toast, the map
window and the main window, then add one window with a nav list and five pages built on the
`TrayApp` actions that already exist. No new Core code except two settings.

## Steps
1. `InfoToast.cs` → `InfoCardPanel.cs` (UserControl with `Render(Snapshot?)`, `Show(kind, issue)`
   logic, repair links) and a thin `InfoToast` host. Verify on the VM that the card looks the same.
2. `NetworkMapForm.cs` → `NetworkMapPanel.cs` + thin host with the `tab` parameter.
3. `Model/Settings.cs`: `MainWindowState MainWindow`, `string TrayClick = "panel"`.
4. `MainForm.cs`: nav, page host, size/position persistence, F1 → help topic per page, Esc hides,
   `Theme.Apply`, `Native.Dress`. Close → hide.
5. Pages: `OverviewPage` (InfoCardPanel + action bar), `ProfilesPage` (DarkListView + buttons),
   `ToolsPage` (buttons + result box), `MapPage`, `LogPage` (tail + incidents + Open folder).
6. `TrayApp.cs`: `ShowMain(page?)`, menu item *Open NetPaw window*, `MouseDoubleClick` → main,
   `TrayClick` setting honoured in the click handler, `--main` in `Program.cs`, `RefreshState`
   notifies the window. `SettingsForm`: *Tray click opens* combo.
7. `QuickPanel`: item "Open NetPaw window" (`q` empty or `Word(q, "window", "main", "open")`).
8. Help: `main-window.md` (new topic), `overview.md` (ways in), `settings.md`. README bullet +
   screenshot from the VM.
9. Tests (Core): `MainWindowStateRoundTrips`, `MainWindowStateClampsToScreen` (pure clamp
   function), `TrayClickDefaultsToPanel`. UI: VM screenshots of every page.
10. Version 0.12.0. PRs: `refactor/panels` (1–2), `feat/main-window` (3–7), `docs/main-window` (8–10).

## Verification
- `dotnet test` ≥ 219 tests, 0 failed. Portable NetPaw.exe ≤ 2 MB.
- VM 150: the info card renders as before (compare with `docs/info.png`). `NetPaw.exe --main`
  opens the window on Overview. each page screenshot. apply a profile from the Profiles page and
  see the status line update. trace to 1.1.1.1 prints hops inside Tools. close hides, tray
  double-click brings it back at the same size and page.
- README shows the main window screenshot (fleet rule).

## Risks
- The refactor of `InfoToast` changes pixel layout → compare screenshots before and after on
  the VM. the toast's `AutoPin`, `Present`, `Render` signatures stay.
- `NetworkMapPanel` inside a `SplitContainer` loses its status bar → the panel keeps its own
  `_status` label. the host forms add nothing.
- A hidden main window keeps a subscription to every `RefreshState` → re-render only when
  `Visible`. unsubscribe on dispose.
- Saved bounds off-screen after a monitor change → clamp to the nearest screen on load (tested).
