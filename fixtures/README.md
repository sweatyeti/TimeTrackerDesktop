# Compatibility fixtures

JSON session files from **TimeTrackerConsole (TTC)**, captured so TimeTrackerDesktop's persistence
layer is built against real TTC output instead of an assumed schema. Each file is read by
`tests/TimeTrackerDesktop.Persistence.Tests/JsonCompatibilityTests.cs`, and the contract they encode
is documented in [`docs/JSON-COMPATIBILITY.md`](../docs/JSON-COMPATIBILITY.md).

## Emitting builds

| Build | Commit | What it emitted |
|---|---|---|
| TTC `main` (v1.2.0) | `7a5e486` | format reference — identical file format to `dev` |
| TTC `dev` | `d9c0e5b` | `session-v2-*`, the hostile-name fixtures |
| TTC v1.1.0 | `e8afbe1` | `session-v1-completed.json` — the last build before `isDeleted` |

`dev` and `main` write the **same format** (both `schemaVersion: 2`, same serializer options, same
`EntrySnapshot` record). They differ only in how they *read*: `dev` adds `SnapshotNormalizer` and
refuses a `schemaVersion` outside `1..2`, which `main` does not have.

## Provenance

Every fixture is either verbatim TTC output, a documented text-level edit of verbatim TTC output, or
hand-authored. Nothing was re-serialized through another JSON writer — that would lose TTC's escaping
(`\u003C` rather than `<`) and make the fixture lie about the wire format.

| Fixture | Origin | Demonstrates |
|---|---|---|
| `ttc-schema-v1/session-v1-completed.json` | verbatim, `e8afbe1` | v1: no `isDeleted` key; ended session |
| `ttc-schema-v2/session-v2-completed.json` | verbatim, `d9c0e5b` | v2 baseline; `endedAt` set; `-05:00` offsets |
| `ttc-schema-v2/session-v2-unfinished.json` | verbatim, `d9c0e5b` | `endedAt: null`; one entry still open (`endTime: null`, `isComplete: false`); `+00:00` offsets |
| `hostile-task-names.json` | text edit of the unfinished emission (session `name` replaced with a plain one) | brackets, quotes, Unicode and path-hostile characters in `task`, stored escaped |
| `filename-hostile-session-name.json` | verbatim, `d9c0e5b` | `name` = `a:b*c?d<e>f|g/h\i` → emitted as `abcdefghi.json` |
| `filename-hostile-emptied-slug.json` | verbatim, `d9c0e5b` | `name` = `:*?<>|/\` → slug is empty → emitted as `session.json` |
| `unfinished-entry.json` | text edit of `session-v2-completed.json` | an ended session that still holds one open entry |
| `deleted-entry.json` | text edit of `session-v2-completed.json` | a soft-deleted entry retained in `entries` |
| `id-gaps.json` | hand-authored | non-contiguous ids (`1, 4, 9`) |

`hostile-task-names.json` keeps the real HTTP-safe task strings but not the hostile session name, so
task-text hostility and file-name hostility are pinned by separate fixtures.

The two text edits are single-field substitutions made directly on TTC's bytes; the escaping in every
string is still TTC's own.

### Why one fixture is hand-authored

No current TTC build emits a gap, so `id-gaps.json` cannot be captured from one:

- v2 soft-deletes: the entry stays in the file and keeps its id.
- v1 hard-deleted (`_timeEntries.Remove(...)` in `e8afbe1`), which did leave real gaps, but that
  behaviour is gone.

The fixture exists because the format is user-editable and the id → entry mapping is keyed rather
than positional, so a reader must tolerate holes. Its timestamps also use short fractional seconds
(`.1`, `.2`) on purpose — a reader must not depend on TTC's usual 7-digit precision.

## Reproducing an emission

The fixtures came from driving TTC's interactive Spectre path in tmux and letting `EntryStore` write
its own file (it writes to `entries/` relative to the process working directory, and flushes on a
~5s timer or at session end):

```bash
git -C /home/hermes/TimeTrackerConsole worktree add /tmp/ttc-v1 e8afbe1   # for the v1 fixture
dotnet build /home/hermes/TimeTrackerConsole -v q --nologo
tmux -L ttcfix new-session -d -s emit -x 140 -y 45
tmux -L ttcfix send-keys -t emit "cd /tmp/out && export TZ='America/Chicago' && \
  TERM=xterm-256color dotnet /home/hermes/TimeTrackerConsole/bin/Debug/net10.0/TimeTrackerConsole.dll \
  new --name 'Session 2026-09-18 15:10:00'" Enter
# type the task, then enter the menu; item 0 starts the next entry, 4x Down + Enter stops and exits
```

Notes that cost time to learn:

- **`TZ` decides the offset in the file.** TTC writes `DateTime.Now` (kind Local), so a fixture
  emitted on a UTC host carries `+00:00` and one emitted with `TZ=America/Chicago` carries `-05:00`.
  Both appear above, deliberately.
- A fixture whose `sessionId` is not a real GUID, or whose keys are not camelCase, is unreadable to
  TTC — it reports "No previous sessions found", which looks like a listing bug.
- Drive the app with `tmux send-keys -l` for literal text; capture with `capture-pane -p` and read
  from the TOP of the pane — the bottom rows of a tall pane are blank and read as "the app died".