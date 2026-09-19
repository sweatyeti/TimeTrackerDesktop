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
6. **`PublishTrimmed=false` stays.** The VS template trims on non-Debug; WinUI is reflection-heavy and
   the plan prefers a diagnosable payload, so trimming stays off.
7. **The repository stays public.** The plan and the provisioning card said private; the user confirmed
   public, consistent with `sweatyeti/TimeTrackerConsole`. Consequence accepted: no credentials or
   host-specific paths may ever be committed.

Still open: the `github-legacy` skill (deleted 2026-09-18) and the VM scratch directory (cleaned
2026-09-18) — **all ten decision questions are now answered.**

## Task 1.3 — entry editing rules (2026-09-18)

**Shape: one operation owns the rules.** `UpdateEntry(id, EntryEdit)` is the only place a task,
description or logged state is written. `EntryEdit` uses `null` for "leave this field alone", so a
caller states only what it wants changed — which is what lets a description-only edit leave the task
untouched. `UpdateActiveEntry(edit)` is the live-edit path for the current-entry panel: it resolves
the active entry itself and otherwise reports `NoActiveEntry`, so a view model never re-derives "which
entry is running" (invariant 9).

**Why one operation rather than three setters:** TTC's `ApplyEntryUpdate(entryId, logged, task,
description)` applies all three fields in a single atomic block (the fix for its own half-applied
update), and this task's objective is to *centralize* the mutation rules. Three setters would give the
shared eligibility rules three chances to drift apart.

**The rules, and where each came from:**

- **Task — trim, blank maps to `none`.** The plan's rule, and the same normalization TTC's *insert*
  path uses. **TTC's update path trims only**, so it can write an empty-string task, which becomes a
  task group named `""`. Our reader still preserves such a value byte-for-byte when it arrives from a
  TTC file; we never create one ourselves.
- **Description — trim, blank stays an empty string.** TTC trims descriptions on update; the plan adds
  that blank text is preserved as an empty string rather than mapped to `none`, because a description
  has no "no value" meaning of its own.
- **Logged — completed, named, non-deleted only.** TTC's `HasLoggedState` is `IsComplete && !IsNoTask`
  (case-insensitive, so "None" cannot log a phantom group); the plan adds non-deleted. The check lives
  in the service as well as being offered as `TimeEntry.CanHoldLoggedState`, so a caller that forgets
  cannot write a logged state that cannot exist.
- **Deleted entries are read-only** for every field until restored.
- **No time editing.** There is no start/end operation in the public API, and a reflection canary
  (`The_public_domain_api_exposes_no_start_or_end_time_editing`) fails if one is ever added.

**Refusals are named, not swallowed.** `EntryEditResult.Outcome` distinguishes `Applied`, `Unchanged`
(valid, but the entry already holds those values), `EntryNotFound`, `NoActiveEntry`, `EntryDeleted`,
`LoggedNotApplicable` and `NothingToDo`, with a `Reason` for a status line. The plan asks for "a
deliberate domain validation result rather than silently changing state" — a `void` setter could not
express any of that.

**Removed:** `UpdateTask(string?)` and `UpdateDescription(string?)` (active-entry, `void`). They were
placeholders whose result contracts this task owns; leaving them beside `UpdateEntry` would have given
the same field two write paths with two chances to disagree.

**Confirmed 2026-09-18:** the blank-task rule stands as the plan states it — a blank task edit writes
`none`. Literal TTC parity (trim only, which can create a `""` task group) was offered and declined,
so it should not be "fixed" back later.

## Task 1.4 — deletion, restore, grouping and Log Group (2026-09-18)

**Projections live outside the service.** `Projections/TaskGroupProjection.cs` and
`Projections/SummaryProjection.cs` are pure functions over a snapshot: no clock, no disk, no XAML, no
mutation surface. A panel renders them and cannot change state while doing so.

**`SessionSummary` vs `SummaryProjection`:** the factory class keeps the plan's name; the record it
returns is `SessionSummary`, because a record and a static class cannot share a name.

**Durations round per entry, then sum — TTC's rule.** TTC computes
`Math.Ceiling((EndTime - StartTime).TotalMinutes)` for each entry and sums those results. Two
30-second entries therefore total **2 minutes**, not the 1 minute that rounding the summed duration
would give. A test pins this, because "round the total" is the natural thing to write and it would
disagree with the console by up to a minute per entry.

**Untracked time gets a row but never a total.** TTC's issue #15: the `none` group is displayed and
excluded from the named totals. An empty-string task — reachable only from a TTC file, since TTC's
update path trims without mapping blank to `none` — is treated the same way for totals, and groups
separately from `none`, as TTC does, because they are different task strings.

**Canonical spelling: the plan's rule, not TTC's algorithm.** TTC groups on `entry.Task.ToLower()` and
displays that lowercased key, so work typed `Client Work` is shown as `client work`; the row order
depends on `Dictionary` enumeration, which v1's hard deletes could perturb. The plan's Q2 settles on
the first non-empty original spelling by start time then id — readable and deterministic.

**Log Group returns the ids it logged.** TTC's `ApplyLogTaskGroup` returns a bare `bool`, which cannot
distinguish an unknown group from one that was already logged. An empty id list means nothing needed
logging.

**A bug the tests caught:** the first `LogTaskGroup` checked `CanHoldLoggedState` (completed and
named) but not `!IsDeleted`, so a deleted entry with a matching task would have been logged. TTC's own
filter checks `!entry.IsDeleted`; the test asserted it and failed, so the rule now matches.

**Delete and restore return `EntryVisibilityResult`** with named refusals (`NotDeletable`,
`NotDeleted`, `EntryNotFound`) instead of the previous silent `void` no-ops.

**Removed:** the bootstrap `Summary()` dictionary. Nothing referenced it and it applied none of TTC's
rounding or untracked rules — a second, subtly different answer to the same question.

## Task 1.5 — resume normalization and chooser metadata (2026-09-18)

**Resume is not "start".** `SessionResumeService.Resume(state, clock)` is a separate entry point from
`SessionService.StartNewSession`, which begins tracking immediately. A resumed session mints nothing
and restamps nothing: a running entry keeps its original start time (invariant 6), the next id is
`max(stored id) + 1` (which is what makes v1's id gaps safe), and a session with no unfinished entry
opens **idle** rather than creating one.

**A corrupt session is refused, not repaired.** More than one unfinished non-deleted entry means
nothing in the file says which one is running. Plan Q3 settles this as a refusal, so `Resume` returns
`CorruptMultipleUnfinishedEntries` with the offending ids and a **null** `Session` — there is no object
to open read-only, repair, or offer for export. The check runs before the service is constructed,
because the constructor throws on this and a throw is not something a chooser can render.

**Deleted entries never make a session corrupt.** Active state comes from non-deleted incomplete
entries, so a deleted-and-unfinished stub (reachable in a hand-edited or damaged file) is ignored
rather than counted as a second running entry.

**Chooser metadata — the user's answer (2026-09-18).** The plan said "duration **or** entry count" and
never picked one; TTC's chooser shows neither. The user asked for the session's **start time** and the
**amount of tracked time**, with no entry count.

**What "tracked time" means here, and why it differs from the summary's totals:**

- it covers every completed, non-deleted entry, untracked (`none`) time **included** — this is the
  session's own total, whereas the summary's named totals deliberately exclude untracked time because
  they answer a different question;
- it rounds each entry up before summing, reusing the summary's rule so the two can never disagree
  about how long an entry was;
- the running entry is excluded, so the number does not creep upward while the chooser sits open.

**Ordering is reproducible, not incidental.** Newest first by start time, ties broken by name ordinal,
so the order never depends on the order the sessions happened to be listed in.

## Task 2.1 — the TTC-compatible JSON serializer (2026-09-18)

**Reading uses `JsonNode`, not a typed deserialize.** The point is to keep the fields we do *not*
understand: a typed reader drops them silently, and an older build sharing a session directory with a
newer one would then delete the newer build's data on the next save. Unknown envelope and entry fields
are retained, and retained entry fields are re-attached by **id**, because ids are keys that survive both
edits and soft deletes.

**A retained field can never override a known one.** Known fields are written first, in TTC's own key
order, and a colliding key is skipped — so a file's stale copy of a field we are responsible for cannot
resurrect itself.

**The encoder bug the fixtures caught.** System.Text.Json's `DateTime`/`DateTimeOffset` writers bypass
the encoder, so TTC writes a zero offset as a **literal plus**. The default encoder escapes a plus sign
as a Unicode escape, so a writer that builds the JSON as a tree of string values emits an escaped offset
and stops matching TTC byte for byte. The writer therefore uses `Utf8JsonWriter` with
`WriteString(name, DateTimeOffset)`. Without the fixtures this would have shipped as a silent wire-format
divergence — and it is now documented in `docs/JSON-COMPATIBILITY.md`, which had asserted the encoder
rule without noticing the timestamp exception.

**A second, wrong serializer was in the tree.** The Task 0.2 bootstrap `JsonSessionStore` carried its own
copy built on `JsonSerializerDefaults.Web`: escaped offsets, unknown fields dropped, no validation. It now
delegates to `TtcJsonSerializer`, so the project has one JSON answer instead of two.

**Byte-identical round trips are asserted.** Re-writing a parsed v2 fixture reproduces TTC's bytes exactly,
including the Unicode escaping and the trimmed fractional seconds. The v1 fixture is the deliberate
exception: writing upgrades it to v2 and makes `isDeleted: false` explicit.

**`isValid` is dropped, not retained.** It is TTC's runtime sentinel and was never part of the format, so
treating it as "unknown" would round-trip a field that does not exist.

**Line endings follow the platform, exactly as TTC's do.** System.Text.Json's indented writer defaults to
`Environment.NewLine`, so a Linux emission uses LF and a Windows one uses CRLF. The first Windows run of
the round-trip test failed on precisely this: the fixtures were emitted on Linux, so a raw byte comparison
was testing the operating system. The behaviour is left as parity (TTC does the same) and the test now
compares content with newlines normalised.

**Refusals are named, and the file is never touched.** `TryRead` reports why a file was refused —
unsupported `schemaVersion`, a `sessionId` that is not a GUID, malformed JSON — while `Read` throws
`JsonException` for malformed JSON and `InvalidDataException` for the rest. A `schemaVersion` of `0`, which
is what a *missing* key reads as, is refused: "absent" is not "version 0 is fine".

## Task 2.2 — atomic session storage and the flush coordinator (2026-09-18)

**Storage is per-user, never the working directory.** `AtomicSessionStore.DefaultDirectory` resolves to
`%LOCALAPPDATA%\TimeTrackerDesktop\entries` (the XDG data folder on Linux). TTC writes `entries/` relative
to the process working directory, so the same session turns up in a different place depending on where the
app was launched from — unusable for an unpackaged WinUI app that can be started from anywhere.

**The write is atomic by construction.** Serialize first, so a JSON failure touches nothing on disk; then
write `<name>.json.tmp` in the **same** directory, `Flush(flushToDisk: true)` — not plain `Flush()`, because
moving the file into place is only atomic if the bytes are already durable — then
`File.Move(..., overwrite: true)`. A crash leaves either the previous file or the complete new one. The same
directory matters: a temporary file on another volume cannot be moved atomically, and the move would
silently become a copy.

**Cleanup only ever deletes `*.json.tmp`.** Litter left by a crash is the problem it solves; it must never be
able to remove something a user might still want.

**The snapshot is taken under the caller's boundary; the write happens outside it.** The caller passes the
same object its mutations are made under, so a flush cannot capture a half-applied change — and cannot stall
the UI for the length of a disk write. A test asserts both halves with `Monitor.IsEntered`.

**A failed flush is never reported as success.** The dirty flag survives, `LastFailure` keeps the error as a
durable UI-visible state, and the next flush retries. `FlushResult` separates `Written` from `NotDirty` so
"nothing to save" cannot be mistaken for "saved".

**Shutdown forces a final flush.** `RunAsync` catches cancellation and flushes once more, which is what makes
the last few seconds of tracked work durable. A clean session is not rewritten.

