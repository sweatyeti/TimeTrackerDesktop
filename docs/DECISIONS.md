# Decisions

- v1 targets Windows 11 with .NET 10 and WinUI 3. Linux work is limited to domain, persistence, fixtures, and CI scaffolding.
- Distribution is an unpackaged, self-contained win-x64 folder inside a versioned GitHub Release ZIP; no MSIX or installer.
- TTC compatibility baseline is origin/main at 7a5e486 (v1.2.0). origin/staging was d2cfa86 when this work began. <!-- TTC's integration branch was renamed `staging` -> `dev` on 2026-09-14; older references to `staging` mean the same branch. This repository's integration branch is also `dev`. -->
- Persistence uses DateTimeOffset at JSON boundaries and preserves the TTC camelCase schema. Runtime-only `isValid` is not serialized.
- The WinUI project and Windows App SDK package cannot be restored or validated on this Linux host because no Windows App SDK workload/toolchain is installed; Windows integration remains a blocker for a Windows runner. <!-- Resolved 2026-09-18: the blocker was the absence of a Windows host, not the code. The `timetracker-win11` VM on frodoid now provides .NET 10.0.401 plus Visual Studio 2026 with the Windows App SDK C# components, and a minimal unpackaged WinUI 3 project compiles there with the XAML compiler emitting App.g.cs, MainWindow.xaml.xbf and XamlTypeInfo.g.cs. Linux still cannot compile WinUI (`XamlCompiler.exe: Exec format error`). -->

## Task 0.2 — solution, projects and pinned baseline (2026-09-18)

Versions below were **resolved by a real restore/build on the Windows toolchain** (`timetracker-win11`,
VS Community 2026) before being pinned. Nothing here was guessed.

- **Windows App SDK `1.8.260804001`**, pinned centrally in `Directory.Packages.props`. Discovered by
  restoring a throwaway WinUI project with a floating `1.*` reference, then pinning what resolved.
  `Microsoft.Windows.SDK.BuildTools 10.0.26100.4654` comes in transitively and is pinned centrally
  too so the resolved version is visible in review.
- **Target framework `net10.0-windows10.0.26100.0` with `TargetPlatformMinVersion 10.0.22000.0`.**
  The stock VS template uses `net10.0-windows10.0.19041.0` / min `10.0.17763.0`, which admits
  Windows 10. Because this product is Windows 11-only, the platform version was raised to a Windows
  11 SDK and the minimum to Windows 11 21H2 — and that pairing was confirmed by an actual build
  before being committed. The installed Windows SDK on the VM is `10.0.28000.0`.
- **Unpackaged, no MSIX.** `WindowsPackageType=None` with `EnableMsixTooling=false`, and
  `launchSettings.json` carries only an unpackaged profile. `EnableMsixTooling` is switched off
  rather than left at the template default because v1 explicitly excludes MSIX; leaving it on would
  activate MSIX packaging tooling for a project that must never produce a package.
- **Portable publish settings live in `Properties/PublishProfiles/win-x64.pubxml`**, not in the
  project file, so Debug builds stay simple: `SelfContained=true`, `WindowsAppSDKSelfContained=true`,
  `PublishSingleFile=false`, **`PublishTrimmed=false`**. The stock template trims on non-Debug
  builds; trimming is disabled here because WinUI is reflection-heavy and the plan prefers a payload
  that is easy to diagnose, with no size pressure at this scale.
- **Central package management** is on (`Directory.Packages.props`), so package versions appear once
  for the whole repository.
- **`global.json` stays at `10.0.100` with `rollForward: latestFeature`.** An exact pin is
  deliberately avoided: this repository builds on the Linux host (10.0.112), the Windows VM
  (10.0.401) and `windows-latest`/`ubuntu-latest` CI, and an exact pin would break the older of
  those. `latestFeature` selects the newest installed 10.0.x feature band on each host, and
  `allowPrerelease: false` keeps stable-only.
- **`tests/TimeTrackerDesktop.App.Tests` targets the Windows TFM** because it references the WinUI
  application to test view models. Consequence, recorded here so it is not mistaken for an
  oversight: the Linux CI job builds and tests only the non-UI projects, and app/view-model tests
  run on Windows CI (plan Task 7.1).

## Task 1.1 — domain models and injectable clock (2026-09-18)

The Phase 0 bootstrap had every domain type in a single `Models.cs`. It is now split into the layout
the plan specifies (`Models/`, `Services/`), and the rules that were implicit in it are explicit.

- **Nullable `EndTime`, never `DateTime.MinValue`.** "Still running" is represented by `null` rather
  than a sentinel, so an open entry cannot leak `MinValue` into a duration or a total. `Duration` is
  `TimeSpan?` and is `null` while open, for the same reason: a zero-length interval would be a wrong
  answer, not a missing one. This also matches what TTC actually writes — `endTime` is `null` unless
  the entry is complete (see `docs/JSON-COMPATIBILITY.md`).
- **`NextEntryId` is `max(existing Id) + 1`, not the entry count.** Ids are keys and v1's hard delete
  left real gaps, so counting entries would re-mint an id that is already in the file. Soft-deleted
  entries keep their id, so their slots are never reused. This is exactly TTC's resume rule
  (`ReseedId(maxId + 1)`), including its limit: an id that was hard-deleted off the end of a v1 file
  can be minted again, because the file itself carries no record that it ever existed.
- **Folders organise, the namespace stays flat.** Every domain type is in `TimeTrackerDesktop.Domain`
  regardless of `Models/` or `Services/`. The bootstrap was flat, TTC is flat, and sub-namespaces
  would add `using` noise to every consumer for no encapsulation benefit at this size.
- **Session names follow TTC's rule, with one deliberate deviation.** A blank name is generated as
  `Session yyyy-MM-dd HH:mm:ss`; the timestamp is formatted with the **invariant culture**, whereas
  TTC uses the current culture. A session's name feeds its file name, so a locale-dependent name
  would move where a session is written for no user-visible gain. The format string keeps "Session"
  *outside* the pattern on purpose: `s` is a reserved format character (seconds), so
  `"Session yyyy-…"` as a single pattern would render the seconds value in place of the word.
- **A whitespace-only name counts as blank and is generated (user decision, 2026-09-18).** A name of
  only spaces slugs to `---.json`, so it is treated as blank instead of being kept.
  <!-- Superseded 2026-09-18. Originally: "**A whitespace-only name is kept, not replaced.** TTC tests
  `IsNullOrEmpty`, not `IsNullOrWhiteSpace`. Preserved deliberately — 'tidying' it here would change
  the file a session produces." The user chose the tidier behaviour after seeing that the file name
  becomes `---.json`. -->
- **`SystemClock` returns a local offset and must never return `UtcNow`.** TTC stamps local time and
  persists the offset in every timestamp it writes, so the offset is data. `IClock` documents this
  so a future implementation cannot quietly break it.
- **`FakeClock` lives in the test project, not the domain.** The domain only needs the `IClock`
  interface; a deterministic clock is a test concern, and keeping it in tests means no fake ships in
  the domain assembly.
- **`SessionService` was relocated, not redesigned.** Task 1.1 moves it out of the bootstrap file and
  onto the immutable model API while keeping its transitions byte-for-byte equivalent, so the move is
  verifiable on its own. Task 1.2 implements the real transitions (`StartNewSession`, `StartEntry`,
  `StopCurrentEntry`, `RestartEntry`, `StopTracking`, `EndSession`) and their immutable results.

## Task 1.2 — Play, Stop and end-session transitions (2026-09-18)

Implements invariants 2–5 of the plan as explicit operations that return an immutable result.

- **The four tracking operations have distinct contracts (user decision, 2026-09-18).**
  - **Start** (`StartEntry`) — the operation for when time is *not* tracked. Calling it while already
    tracking is a **no-op** (`SessionChange.None`): splitting a running entry because a stale command
    arrived would rewrite the user's data, whereas doing nothing is recoverable.
  - **Stop and start** (`RestartEntry`) — the operation for when a task *is* active: completes the
    running entry and opens a new one. It also starts when idle, because the command surfaces that
    mean "switch to a new task" (the widget's start-new-task control, the tray's Start new task) must
    work from idle; the reported change distinguishes `EntryRestarted` from `EntryStarted`.
  - **Stop** (`StopCurrentEntry`, and `StopTracking` as its command-surface name) — stops and does
    **not** start a new task. A no-op when nothing is running, so a double Stop cannot move an end time.
  - **Stop and exit** (`EndSession`) — stops any active work, then stamps `EndedAt`. Confirming,
    flushing and exiting belong to the caller, not the domain.
  <!-- Superseded 2026-09-18. Originally: "**Play and Restart are one transition, not two.** The plan
  states Play 'uses the exact restart semantics', so `StartEntry` and `RestartEntry` both call a
  single private `OpenEntry`: complete whatever is running, then open a new entry and report
  `EntryStarted` or `EntryRestarted` according to what actually happened. Two implementations of one
  transition eventually disagree; the two names exist because the widget's Play and the tray's 'Start
  new task' are separate command surfaces." The user instead specified four operations with distinct
  conditions. -->
- **The widget's Play has to dispatch**: idle calls Start, active calls Stop and start. The plan's
  "Play uses the exact restart semantics" is satisfied by that dispatch, not by Start silently
  restarting behind the caller's back.
- **`StopTracking` is `StopCurrentEntry`.** TTC's "Stop tracking" (`StopSession(exit: false)`) is
  literally `StopCurrentEntry()`, so the command-surface name delegates rather than reimplementing.
- **`EndSession` restamps `EndedAt` on every call (TTC parity — user decision, 2026-09-18).** A repeat
  call rewrites when the session ended, so a caller that must not move it has to check
  `SessionResult.IsEnded` first. <!-- Superseded 2026-09-18. Originally: "**`EndSession` is
  idempotent.** A second call is a no-op that leaves `EndedAt` untouched. **TTC restamps `EndedAt` on
  a second call**, which silently rewrites when a session ended; the plan asks for idempotency, so we
  deviate deliberately." The user chose TTC parity over protecting the original end time. -->
- **Stop is a no-op when nothing is running**, so a double Stop cannot move a recorded end time.
- **A new session starts tracking immediately (TTC parity — user decision, 2026-09-18).**
  `StartNewSession` opens an entry stamped at the current time with the `none` task; the caller then
  prompts for a task and applies it with `UpdateTask`, which leaves the original stamp intact. TTC
  stamps *before* showing its prompt, so the time spent answering the prompt is tracked rather than
  lost. <!-- Superseded 2026-09-18. Originally: "**A new session starts idle.** TTC's CLI `new` opens
  an entry immediately because it prompts for a task up front, but the widget has a real idle state
  ('No task running', Play available, Stop unavailable) and the plan requires a session with no active
  entry to stay idle. A caller wanting TTC's behaviour calls `StartEntry` straight after." The user
  chose TTC's CLI behaviour. -->
- **Opening an entry reopens an ended session** (`EndedAt` back to `null`). This follows TTC's own
  `Resume`, whose comment is "resuming means the session is active again".
- **`SessionResult`/`SessionChange` are the status-cue contract.** Each operation returns the new
  state, what changed, and the entry it touched, so a view model can update the tray icon, the
  ACTIVE/NOT ACTIVE banner and the tooltip without diffing two states. `AffectedEntry` is `null` for a
  no-op because nothing was touched.
- **`UpdateTask`, `UpdateDescription`, `Delete` and `Restore` stay `void` for now.** Their result
  contracts belong to Task 1.3 (editing rules); giving them a shape here would pre-empt that design.

## User decisions on pending behaviour (2026-09-18)

The user was asked to settle the behaviour forks this work had been deciding unilaterally. Answered
so far:

1. **New session start state — start tracking immediately** (TTC's CLI `new`), prompting for a task up
   front. Implemented: `StartNewSession` opens an entry at the current time; the caller prompts and
   applies the task via `UpdateTask`.
2. **Generated session name locale — invariant culture.** Keeps a session's name, and therefore its
   file name, from shifting with the machine locale. Unchanged from the first implementation.
3. **Ending a session twice — match TTC and restamp `EndedAt`.** Reverses the original idempotent
   implementation.
4. **The four tracking operations are distinct, not aliases.** Start applies when time is not tracked
   (a no-op if it is); Stop-and-start applies when a task is active; Stop stops without starting a new
   task; Stop-and-exit confirms, stops, saves and exits. Implemented as `StartEntry`, `RestartEntry`,
   `StopCurrentEntry`/`StopTracking` and `EndSession` — with confirmation, flushing and exiting left to
   the caller. This reverses the original "Play and Restart are one transition" implementation, and it
   means the widget's Play button must dispatch on state rather than always calling `StartEntry`.
5. **A whitespace-only session name counts as blank and is generated.** Deviates from TTC's
   `IsNullOrEmpty` test, because a name of only spaces slugs to `---.json`.

Still open (asked one at a time, not yet answered): `PublishTrimmed=false`, repository visibility (the
plan says private, the repo is public), how to reconcile `main`'s unrelated history at promotion, the
`github-legacy` skill still in the Telegram index, and the leftover VM scratch directory.

