# TTC JSON compatibility baseline

What TimeTrackerDesktop must read and write, established from **TimeTrackerConsole's actual output**
rather than from its implementation read quickly. Every claim below is backed by a fixture in
[`fixtures/`](../fixtures), by TTC source at a named commit, or by a test in
`tests/TimeTrackerDesktop.Persistence.Tests/JsonCompatibilityTests.cs`.

## Selected TTC commits

| Ref | Commit | Why it matters here |
|---|---|---|
| `main` (v1.2.0) | `7a5e486` | current release; **same file format** as `dev` |
| `dev` | `d9c0e5b` | current integration head; adds the reader-side validation below |
| v1.1.0 | `e8afbe1` | last release **before** `isDeleted`; the v1 schema emitter |

The format is identical on `main` and `dev` — both write `schemaVersion: 2` with the same serializer
options and the same `EntrySnapshot` record. They differ **only in how they read**, which is the
subject of [Reader behaviour: main vs dev](#reader-behaviour-main-vs-dev).

## Where files live

- Directory: `entries/` **relative to the process working directory**, created on demand.
- Name: one file per session, `<slug>.json`, where `Slugify` replaces spaces with `-`, strips every
  path/device-hostile character (the platform's invalid set **plus** the Windows set, so files stay
  portable), and falls back to `session` when nothing survives. A collision gets `-2`, `-3`, …
- Writes are atomic: a transient `<slug>.json.tmp` is written, flushed, then `File.Move(..., overwrite:
  true)` onto the final name. A crash can leave a stale `.tmp`, never a half-written session.
- The flush loop wakes every 5s and writes only when a mutation has raised the dirty flag, so **the
  file lags the screen** — read it after a graceful exit, not during a session.
- Files are identified by **content**, never by file name: two sessions with hostile names can share
  one file name, and the name inside the file is the user's original text.

## Serializer settings (wire format)

| Setting | Value | Consequence |
|---|---|---|
| Encoding | UTF-8, **no BOM** (`new UTF8Encoding(false)`) | a BOM would make the file unreadable to TTC |
| `WriteIndented` | `true` | 2-space indent, no trailing newline; the file ends with `}` |
| `PropertyNamingPolicy` | `CamelCase` | every key is camelCase, first letter lowercase |
| Encoder | **default `JavaScriptEncoder`** | `"`, `<`, `>`, `&`, `'` and **all non-ASCII** are stored as `\uXXXX` escape sequences |

The encoder is the detail most likely to be got wrong. A real emitted file contains
`"task": "caf\u00E9 \u2615 \u65E5\u672C\u8A9E ..\\..\\ /etc/passwd :*?|\u003C\u003E"` — not the
literal characters. Hex digits are upper-case. A reader that assumes plain UTF-8 text in the file, or
a writer that emits raw UTF-8, produces a file TTC and we would disagree about.

## Top-level schema

| Field | Type | Nullable | Notes |
|---|---|---|---|
| `schemaVersion` | int | no (but a missing key reads as `0`) | `1` or `2` are readable; `0` means absent |
| `sessionId` | GUID string | no | must parse as a GUID; a non-GUID makes the file unreadable |
| `name` | string | yes in practice | user text; empty/null is repaired to `Unnamed session` on read |
| `startedAt` | ISO 8601 with offset | no | session start |
| `endedAt` | ISO 8601 with offset | **yes** | `null` = the session never ended cleanly |
| `entries` | array | yes in practice | `null`/truncated is repaired to an empty list |

## Entry schema

`v2` is `v1` plus exactly one field. Nothing else changed between the versions.

| Field | Type | Nullable | v1 | v2 |
|---|---|---|---|---|
| `id` | int | no | ✓ | ✓ |
| `startTime` | ISO 8601 with offset | no | ✓ | ✓ |
| `endTime` | ISO 8601 with offset | **yes** | ✓ | ✓ |
| `task` | string | yes in practice | ✓ | ✓ |
| `description` | string | yes in practice | ✓ | ✓ |
| `logged` | bool | no | ✓ | ✓ |
| `isComplete` | bool | no | ✓ | ✓ |
| `isDeleted` | bool | no | **absent** | ✓ |

- `isDeleted` is **additive**: a v1 file has no such key and deserializes to `false`, so v1 needs no
  migration. This is asserted by `V1_fixture_omits_isDeleted_and_it_fields_defaults_to_false`.
- `IsValid` is deliberately **not** persisted — it is a runtime sentinel only.

### Ordering and ids

- Entries are written **ascending by id**.
- Ids are **keys, not positions**: gaps are legal (`id-gaps.json` holds `1, 4, 9`) because the
  original v1 build hard-deleted entries (`_timeEntries.Remove(...)`) and left the hole behind. v2
  soft-deletes instead, so the entry and its id both survive.
- An **open entry is always the highest id** in a real session, because TTC only ever resumes the
  newest entry. A fixture (or a hand-edited file) that violates this is a live hazard: TTC's
  stop-current-entry path looks only at the highest id and silently does nothing.
- Duplicate ids are collapsed on read (last occurrence wins) so that a keyed reader and a positional
  reader cannot disagree about which entry an id refers to.

## Time semantics

TTC stores `DateTime.Now`, which is `Kind=Local`, and System.Text.Json's default handling writes it
as ISO 8601 **carrying that machine's UTC offset**, with up to 7 fractional digits:

```text
2026-09-18T14:39:45.9918525-05:00     emitted by a US Central host
2026-09-18T19:37:42.0659659+00:00     emitted by a UTC host
```

**Decision: model every persisted timestamp as `DateTimeOffset`, not `DateTime`.** Deserializing an
offset-bearing string into a `DateTime` converts the value to the *reading* machine's local time:
the instant survives but the recorded offset is silently replaced (a `-05:00` value read on a UTC box
becomes a `+00:00` `DateTime`). `DateTimeOffset` keeps both, and the same session therefore measures
identically wherever it is opened.

The tests enforce this by comparing the parsed offset against the **literal offset text in the file**,
not against a re-derived value — see `Fixture_timestamps_keep_their_offset_and_instant`.

Two further invariants:

- `endTime` is written as `null` unless the entry is complete (`IsComplete ? EndTime : null`), so an
  open entry never carries a stale end time even though the in-memory `TimeEntry.EndTime` may hold
  `DateTime.MinValue`.
- A `null` `endTime` or `endedAt` is **normal, not corrupt**: it is how TTC records an entry or
  session that was still running. Distinguish "unfinished" from "damaged".

## Absent-data repair rules

The JSON layer is forgiving by construction: any missing key is left at its CLR default, so a
truncated or hand-edited file deserializes "successfully". TTC repairs rather than rejects:

| Missing / broken | Result |
|---|---|
| `name` null or whitespace | `Unnamed session` |
| `entries` null | empty list |
| `null` element inside `entries` | dropped |
| entry `task` null | `none` (see below) |
| entry `description` null | `""` |
| duplicate ids | collapsed, last wins |
| `startTime` missing | stays the default `DateTime`, **not** invented |

Nothing is fabricated for a merely absent field. The single exception is `task`, because `none` is
what the app itself stores for an entry with no task.

## `none` semantics

`none` is the task name for **untracked** time, and it is a value the user can also produce by
clearing a task (`blank means none`). It is therefore indistinguishable from a task literally named
`none`, and it participates in grouping and logging like any other value. Compatible behaviour:
treat it as a normal task string; never treat it as null.

## Soft-delete semantics

- A deleted entry **stays in `entries` in the file** with `isDeleted: true`, deliberately, so it can
  be viewed and restored later.
- Every enumeration, count, group projection and total **must exclude** `isDeleted` entries; the
  entry is only reachable through TTC's "View deleted entries" flow.
- The flag moves alone: `id`, `startTime`, `endTime`, `task` and `description` are untouched.
- The default for a **missing** flag is `false` (v1 files, and v2 files written before a delete), so
  "absent" must never be read as "deleted".

## `schemaVersion` validation

`dev` refuses a session whose `schemaVersion` falls outside the supported range `1..2`:

- **greater than 2** — a future format this build cannot promise to render;
- **less than 1, including `0`** — `0` is what a file with the key *missing* or explicitly `0`
  deserializes to, i.e. an absent or unsupported-old format.

A refused file is **reported, never repaired, moved or deleted**: listing is read-only, and offering
an unsupported file would mean persisting it as a usable session on the next flush. `main` has no
such check.

## Reader behaviour: main vs dev

The plan requires the differences between `main` and `dev` that affect sessions, snapshots or the
store. They are entirely in the reader:

| | `main` (`7a5e486`) | `dev` (`d9c0e5b`) |
|---|---|---|
| File format | v2, identical | v2, identical |
| `SnapshotNormalizer` | absent | present — one `Normalize` + one `TryValidate` shared by every load path |
| Repair | each reader guards its own fields (Resume some, LoadReadOnly others) | single repair step, so load paths cannot drift apart |
| Pre-validation of `schemaVersion` | none | refuses outside `1..2`, with a user-visible reason |
| Load failure reporting | swallowed | `TryLoadSnapshot` returns the reason; skipped files are listed |
| `EntryStore` delta | — | +58/−? lines: `TryLoadSnapshot`, `ListAllSessions` returning `SessionFileListing` (sessions **and** skipped files) |
| `Session.cs` | — | heavily reworked (+543 lines): shared action surface for both UIs, TUI, viewport budgeting |

**Consequence for TimeTrackerDesktop:** build against the `dev` behaviour (single normalizer, explicit
validation, visible failure reasons) and treat the file format as common to both. A file we write
must remain readable by `main`, which it will be as long as it is v2 with camelCase keys.

## Contract for this repository

`TimeTrackerDesktop` must:

1. **Read v1 and v2.** A missing `isDeleted` means `false`.
2. **Write v2** — `schemaVersion: 2`, camelCase, indented, UTF-8 without BOM, no trailing newline.
3. **Preserve instants and offsets** by modelling timestamps as `DateTimeOffset`.
4. **Tolerate** absent `name`/`entries`/`task`/`description`, duplicate ids, and id gaps, applying the
   repair rules above rather than refusing the file.
5. **Refuse only** a `schemaVersion` outside `1..2` or JSON that does not deserialize — and report
   why, without modifying the file.
6. **Retain** unknown fields (see below) and never lose data by round-tripping.
7. **Filter** `isDeleted` entries out of every count, group and total, while keeping them on disk.

### Unknown fields

The fixtures are kept as raw text so tests can assert on the wire format, and so a future unknown-field
check has something to read. `Fixture_field_names_match_the_documented_schema` is a **drift canary**: it
fails if TTC adds, removes or renames a field, forcing this document and the affected reader to be
updated deliberately rather than silently. TimeTrackerDesktop should preserve fields it does not
understand when rewriting a file, so an older build and a newer one can share a session directory.

## Open questions

- **Unknown-field retention is not yet implemented** — Task 0.3 pins the contract and the tests; the
  reader that must honour it arrives with the persistence layer (Phase 1+).
- **`readme` documents the schema to users.** If TTC's README and this document disagree, the emitted
  files win — they are the actual contract, and the fixtures are the evidence.
- **`task` uniqueness is not enforced** anywhere, so grouping must tolerate several entries sharing a
  task and the same task appearing with different casing.