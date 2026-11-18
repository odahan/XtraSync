# XtraSync mechanics 

## Scope and evidence chain

XtraSync implements a local-folder quick comparison and mirror workflow. It does not reproduce the original's full feature set.

## Project layout

- `app/` contains the WPF source project (`XtraSync.csproj`), its build configuration, and the `shots/` screenshot folder. Build intermediates and binaries are generated in `app/obj/` and `app/bin/`.
- `Publish.ps1` and `Publish.bat` are at the project root. The batch launcher locates the PowerShell script beside itself, and the script resolves the source project and output directory from its own location, independently of the caller's working directory.
- `Publish/` receives the production self-contained Windows x64 executable (`XtraSync.exe`). `Publish.ps1` disables symbol generation with `DebugType=None` and `DebugSymbols=false`, and removes any `XtraSync.pdb` left by an earlier publication. Normal development and test builds retain their usual symbol configuration.
- `CREDITS.md` and this document are at the project root.
- `XtraSync.slnx` groups the application and `tests/XtraSync.Tests/`. Root-level `Test.ps1` runs the suite and enforces coverage; `tests/README.md` documents the scope. Reports are generated under `TestResults/`.
- User settings remain in `%LOCALAPPDATA%\XtraSync\` (`exclusions.json` and `folder-history.json`), independently of the source and executable locations.

## Timestamp comparison: implemented

| Original function | Verified behavior | Evidence |
| --- | --- | --- |
| `0x0110F780` | Reads setting `0x12` (timestamp comparison); when enabled reads `9` (ignore DST), `0xB` (ignore time zone), and `0x15` (tolerance). Starts with flags `0`. Provider capability `0x4000` adds left flag `4` or right flag `8`. Calls `0x00996A00`. | `ev_761b4e397471147570d85d69f8113eb18595a2ad01687b45b2ca75b80ce22cd9` |
| `0x00996C20` | A zero FILETIME is unknown. | `ev_1323a1e66877c04c85365280208ec7d51feb55f39d8a1de471ae642b13510a39` |
| `0x009970B0` | `d = abs(left / 10000000 - right / 10000000)`, integer division of each timestamp **before** subtraction. | `ev_308b6f9ae68ffe6de9d972abec3d1dcac7ef3e5738d3aeb626a22a6b5322b347` |
| `0x00996A00` | Returns `0` if either timestamp is unknown; otherwise `-1` for left older, `+1` for left newer, `0` for equality under the rules below. | `ev_57d799ae7aebe3a756c6d59754e4503bd030917f0db1184fe8c7405d8dbd448d` |
| `0x00997320` | Calls Windows `FileTimeToSystemTime` for calendar comparisons. | `ev_a676341a8f9496b3bd0c3af8bb1ce923ceb6638b785f2454b4af338dd27b9af5` |

The comparison returns equality when `d <= tolerance`; the boundary is inclusive. Differences within a second bucket compare equal even when raw FILETIMEs differ.

With flag `1`, it also accepts `abs(d - daylightShiftSeconds) <= tolerance`. The original shift comes from runtime global `0x021490FC`; its initialization has **not** been traced. The core accepts an explicit shift for this branch. The UI does not expose DST handling because its runtime value is unresolved; it does not silently assume 3600.

With flag `2`, and only when integer `d / 3600 < 24`, calculate `r = d % 3600`; if `r > 1800`, replace it by `3600 - r`. Return equality when `r <= tolerance`. Consequently 23 hours plus a small tolerance can match, while 24 hours cannot. The UI exposes this exact rule as “Ignore whole-hour time zone differences.”

The incomplete timestamp mask at `0x00996C14` is `12`; comparison byte at `0x00996C15` is `0`. Reads: `ev_3457fc6339e44055930bb260372f443760e1571022423fa70b05c04e4fc3cf88`, `ev_3c2c34af0b64d056cda289361b65f8b29a5b72366de18c1f71937f532dea2053`. If neither flag `4` nor `8` is set, return the order. Otherwise, only continue for `d <= 86399` and the same UTC year/month/day. A flagged side at midnight matches any time that date. Otherwise hours and minutes must match; a flagged side with second zero matches the other side's seconds. Milliseconds are not examined in this calendar fallback. Local filesystem entries do not set incomplete flags, but the shared core implements them.

`ComparisonRules.CompareFileTimes` implements these branches. The app skips time comparison entirely when its timestamp checkbox is off, so time does not affect the folder status in that mode.

## Settings values

Delphi property metadata contains the following integers immediately before the property index and length/name. Their interpretation as default values is **derived from metadata layout**, not a traced settings constructor. These are the initial application settings; later user changes are session-only.

| Setting | Setting index | Original value address | Value used | Evidence |
| --- | --- | --- | --- | --- |
| TimestampTolerance | `0x15` | `0x010FB67D` | `2` seconds | `ev_8c19ddaa31797abee38e5ba540fca5d3bd3f4778b15dd4eaaca434a5e77467e0` |
| UseSizeComparison | `0x11` | `0x010FB77B` | `1` / enabled | `ev_faaeb2df5a5be8be8fb61090b3b3119347b70441432ba6dc8b77bb436f226d2d` |
| UseTimestampComparison | `0x12` | `0x010FB7B7` | `1` / enabled | `ev_5658cbdfa63b7a0aceedd37d681d3344e8a01647e7c58feec25f1ce2bdb4e695` |
| IgnoreDST | `9` | `0x010FB526` | `0` / disabled | `ev_8b3c5b3f1b0dcbbffd1919b7c5d1464a9f94df0ffccd005ac7f1bdb27a3a019a` |
| IgnoreTimeZone | `0xB` | `0x010FB593` | `0` / disabled | `ev_800901c37bdb2e07ddb85674fce68c6f1f74ab2797c66bcb2ce28edbab4177f7` |

The original flags initializer byte at `0x0110F920` is zero (`ev_916fe0b5ca7d915bc9c4a3ba334ee66ec3f3830bceef2a43e00d221af2c92d63`).

## Mirror actions: implemented

| Original function | Observed rule | Evidence |
| --- | --- | --- |
| `0x01D273F0` / MirrorRightExecute | Calls `0x01D2E610` with mode `4`. | `ev_41d09a91e549eb0045f80dcaef927f7e6ab2e49f9e5954625215193b8b97e7f7` |
| `0x01D27400` / MirrorLeftExecute | Calls `0x01D2E610` with mode `3`. | `ev_890af35a1c21f78bcacc7d2dbe4ef787a38a8d1ef3468f30f1bc81b642ae689b` |
| `0x01D2E610` | Prepares the active session and creates synchronization UI via `0x019A5A00`, forwarding the mode; calls `0x019A6800`. | `ev_379933f1c32d80b7c9cfa05af6587d5b9e2581a824a6e9abec7e965e6597c7e8` |
| `0x019A5A00` | Uses mode-indexed captions/settings; stores mode at synchronization object offset `0x238`. | `ev_76e8345aba4af4f9b603ae03a2009639f18a555b38fd68b720ca4ba157704173` |
| `0x019A6800` | Starts preparation, shows modal review, proceeds when modal result is `1`. | `ev_e8fb3452aeba4ba43e31797959b1e78fec3d7d68573815bbf25cbaa3d495f8ee` |

The original mirror-right message at `0x02BA3C16` explicitly specifies replacing different right items, deleting right orphans, and copying left orphans to the right. Mirror-left at `0x02BA3D7F` specifies the symmetric rule. Evidence: `ev_790602bf76f1be31183c22190ccfbbe3f8a63f646769bec50914b5d325fed377`.

`MirrorMode` preserves identifiers `3` and `4`. `FolderService.Plan` implements those three message-level rules and includes empty directories. `MainWindow` previews every copy/create/delete operation, then requires Apply mirror. The original low-level operation planner/executor has **not** been fully traced; do not describe these as instruction-for-instruction reconstructions.

## Deliberate implementation choices and limitations

- Statuses use presence, file/directory type, verified timestamp order, then optional file size. A file can be “Equal” with different content: this is a metadata comparison. Original status aggregation was read at `0x01118710` and `0x0111A8C0`, but its full enum/bitset semantics remain unresolved. Evidence `ev_572de842efb7c236a3596842f1cb56e516672acdf5fe09fdb23826146fbb314a`, `ev_f4dbc62859b5d21c66d553f0ba36db2a415c53461af4e601156a33e67d59a16b`. XtraSync's display labels and precedence are implementation choices.
- Windows relative names are matched case-insensitively; original case/filter configuration has not been traced.
- Folder scans run off the UI thread, one side at a time, with throttled progress and pause/cancellation checkpoints. UI rows are virtualized.
- Overlapping roots and reparse points are rejected. Preview revalidation rejects unreviewed additions and file metadata/type changes; missing and unreadable scopes are reported and protected under the error policy. Deletes are nonrecursive and ordered deepest first; file copies overwrite the destination and preserve source modification time. These protections are XtraSync choices, not claims about the original. Mirroring is not transactional and can stop after partial work; files can still change during execution.
- No automatic deletion or synchronization occurs on app launch.
- Settings persistence, content/CRC comparison, text/hex/image/table comparisons, merge, saved sessions, archives, remote providers, filters, and full original UI parity remain unimplemented. No original licensing code was copied or modified.

## Build and validation

From the project root, build with `dotnet build app\XtraSync.csproj -c Release -m:1`.
Publish with `.\Publish.bat` (also available by double-clicking in Explorer), invoking the root-level `Publish.ps1`. It produces a self-contained win-x64 single-file executable in the root-level `Publish/` directory. Both publishing scripts can be invoked from any working directory using their full paths.
`app/Directory.Build.props` also resolves its default `PublishDir` to the root-level `Publish/` directory, so direct `dotnet publish app\XtraSync.csproj` commands use the same destination. Use `Publish.bat` or `Publish.ps1` for the production runtime and single-file options.
The publication command explicitly sets `UseSharedCompilation=false` and disables build servers. On this host, builds additionally required execution outside the restricted sandbox because compiler pipes were denied.
The behavior checks referenced in the historical validation results below came from an external `MechanicsChecks` project. That project is not present in the current project layout; those results cannot be rerun from this directory alone.

The current in-repository test suite is in `tests/XtraSync.Tests/`. Run `.\Test.ps1` to build and test the solution with `-m:1`, collect coverage and enforce minimums of 98% of lines and 95% of branches on the application logic. See `tests/README.md` for commands, isolation and coverage exclusions.

The suite also exposed a cancellation race in a paused checkpoint. `OperationControl.Checkpoint` now checks cancellation after its pause gate opens as well as before waiting, so Stop cannot release a checkpoint successfully without noticing the cancellation. Progress throttling accepts an optional `TimeProvider` to test timing and cancellation during a copy deterministically. `MainViewModel` accepts explicit history and exclusion stores so tests cannot touch personal settings; its default constructor keeps the normal application stores.

Current layout validation (2026-11-18):

- Release build succeeded with `dotnet build app\XtraSync.csproj -c Release -m:1 --disable-build-servers`.
- Generated `app/bin/` and `app/obj/` directories were removed and regenerated to discard paths inherited from the previous source location.
- Running `E:\XtraSync\Publish.bat` from `E:\` succeeded and regenerated `Publish/XtraSync.exe` and `Publish/XtraSync.pdb` using the production publishing options.
- MSBuild property evaluation confirmed that the default `PublishDir` resolves to the root-level `Publish/` directory.
- Validation used `DOTNET_CLI_TELEMETRY_OPTOUT=1` in the calling shell because the restricted environment denied the CLI's telemetry registry access. NU1900 warnings remain because the NuGet vulnerability service could not be reached.

Historical validation results on 2026-11-18:

- Direct Release build succeeded with `-m:1`, outside the restricted sandbox.
- All 35 behavior checks passed. Tests operate on generated fixture folders under the check project's build directory.
- `Publish.ps1` succeeded and produced `E:\XtraSync\Publish\XtraSync.exe` (145,343,984 bytes, self-contained win-x64 single file).
- NuGet vulnerability metadata lookup failed with NU1900 because the NuGet service could not be reached; package restore used available packages and compilation succeeded.
- Craft Anything `rebuild_check` was run, but its own build failed on denied access to `\\.\pipe\LOCAL\dotnet_*`. It did not establish mechanics or visual parity. Direct publication success must not be described as a passing automatic check.
- Computer Use refused launching the published app with “Computer Use was not approved to use xtrasync.” No visual verification was completed, and the restriction was not bypassed.

## Progress and two-way synchronization extension (2026-11-18)

These features were requested for XtraSync; they are implementation choices, not verified original behavior.

- Comparison, preview validation and synchronization show their phase, current full file path, file/subfolder counts and total bytes with automatically scaled B/KB/MB/GB/TB units (1024 per step). Scans use an indeterminate bar because the total is unknown; execution shows completed/total operations. Counts reset for each phase and scan totals count both roots.
- Pause/Resume waits at scan, planning and operation checkpoints and between 1 MB copy chunks. Stop cancels pending work and keeps completed operations. Restart stops the current run, waits for it to finish, scans again and requests a fresh preview before any further writes. Paths and options are fixed during a run.
- Subfolders included defaults to true. When disabled, only root-level files are considered; subfolders and their contents are preserved.
- Synchronize Both Way copies missing files/folders in either direction and replaces older versions according to the selected comparison rules. It never propagates deletions. Type conflicts and differing sizes with no newer version are listed in the preview and left unchanged, including descendants of type-conflicting folders.
- File copies use a temporary sibling and replace the destination only after a complete copy. Stop removes the partial temporary file. Previously completed copies or deletions are not rolled back.
- The latest direct app build passed with zero warnings/errors. The expanded behavior checks cover progress totals, pause/resume/stop, restarting from a fresh plan, shallow mirroring, both copy directions, conflict preservation, unit scaling, and cancellation during a copy. rebuild_check passes build and startup screenshot; comparisons with original runtime mechanics remain unverified.

## Persisted exclusions (2026-11-18)

The original shared-pattern behavior below remains the default. The name-filter dialog extension described next adds typed patterns and opt-in mirror destination cleanup.

- The Exclusions field saves immediately to the current user's local application data at XtraSync/exclusions.json and restores on startup. Clearing the field persists an empty list. Missing or malformed JSON falls back to an empty list; write failures appear in the status line.
- Separate patterns with semicolons. Names match at any depth; patterns with a slash match relative paths. Matching ignores case, supports * and ?, and accepts either path separator. Excluded directories are not traversed. The same rules apply to comparison, both mirrors, two-way synchronization and preview revalidation.
- The field includes a visible semicolon-separated example and accessible help. Its tooltip explains pasted line breaks, ignored surrounding spaces, literal spaces/commas, wildcards limited to a single path component, relative paths, excluded folder contents, automatic saving, and clearing the list.
- Mirrors preserve ancestors containing excluded items. A file/folder replacement that would remove an excluded descendant is skipped and reported as a conflict.
- Direct build passed with zero warnings/errors; all 69 behavior checks passed, including persistence, clearing, damaged settings, all plan modes, and preservation of excluded descendants. rebuild_check passed build and startup screenshot; original runtime mechanics comparisons remain unverified.

## Name-filter dialog and upstream exclusions (2026-11-18)

- The funnel button beside Exclusions opens a dark themed Name filters dialog. Shared patterns apply to files and folders; additional lists apply only to files or only to folders. The main exclusion line is authoritative: bare patterns apply to both types, `file:` prefixes select files, and `folder:` prefixes select folders. For example, `*.tmp; file:*.bak; folder:bin` represents one common, one file and one folder exclusion. Opening the dialog parses this line and displays each category as one item per line, without semicolons. Save filters rebuilds the complete main line with semicolons and the type prefixes; Cancel discards the draft.
- Delete excluded items from mirror destination is on the main screen beside the other options. Editing or clearing the dialog never changes this preference.
- Exclusions is an editable combobox. Its current text is saved immediately for both manual and dialog edits. The ten most recent nonempty completed lines are remembered on Enter, leaving the field, choosing a recent item, accepting the dialog, starting an operation or closing the window. Intermediate keystrokes are not history entries. Reusing a line moves it to the front without duplicating it; comparison ignores case, spacing around separators and slash spelling. Clearing the current line persists an empty filter without deleting the saved history.
- Matching runs during the scan. An excluded source file is omitted before size/time comparison and is never opened for content; excluded source folders are never traversed. Enumerating the containing directory still reads names and, for typed rules, entry types. This is upstream filtering rather than reading the excluded tree and discarding it afterwards.
- With default settings, the destination scan applies the same exclusions to preserve ignored target items and their ancestors. Source exclusions act as absent entries in the mirror input, but matching target exclusions are protected.
- The additional Delete excluded items from the mirror destination option is initially false. When enabled for Mirror to right or Mirror to left, only the source scan is filtered; the destination scan is unfiltered within the selected recursion scope. Excluded target entries become ordinary orphans and their deletions appear explicitly in the review. They are deleted only after Apply mirror. Source files are never deleted by this option.
- In Synchronize Both Way, exclusions simply ignore entries on both sides. Destination cleanup is disabled in the generated plan even if its preference is checked; this mode never creates delete operations.
- Preview revalidation uses the same source/destination scan policies as planning. Under cleanup, even changes to excluded target files invalidate the preview; changes inside excluded source folders remain irrelevant. Reparse-point protections are retained: cleanup refuses linked destination entries rather than following them.
- Subfolders included remains authoritative. With it off, root files alone participate and target subfolders are preserved even when cleanup is enabled.
- `exclusions.json` stores the complete editable `ExclusionLine`, the cleanup preference and `History`. Existing JSON strings and the previous typed settings object are migrated without losing their filters or cleanup preference. File and folder patterns from the previous object become `file:` and `folder:` entries in the main line. Saving replaces the settings atomically. Invalid settings fall back to empty exclusions and cleanup off; malformed history entries are ignored, deduplicated and limited to ten.
- `--screenshot <absolute-file.png> --filters` renders an empty filter dialog for visual verification without loading or saving personal settings. The rendered example is `app/shots/name-filters.png`. Windows Computer Use could not start on this host because its runtime rejected a `file:///E:` permission path; the dialog was checked using the application's screenshot renderer instead, not by automated clicks.
- Validation after the editable history extension: all 206 tests passed, with 99.69% line coverage and 97.88% branch coverage in the documented unit-test scope. The published main window and filter dialog were checked with screenshot rendering.
- The publication smoke check exposed a WPF startup failure (`BadImageFormatException` loading `DirectWriteForwarder.dll`) with native-only bundle extraction. `Publish.ps1` now also sets `IncludeAllContentForSelfExtract=true`. This keeps a self-contained single executable while extracting managed dependencies before startup, as described in the [Microsoft single-file documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview). The SDK emits NETSDK1244 for this compatibility mode; startup was successfully verified with the resulting production executable. This extraction behavior is a deployment tradeoff, not a change to synchronization logic.

## Live operation statistics and confirmation (2026-11-18)

- Scanning reports visited files, directories, bytes and current path. Its progress bar is indeterminate because the total has not been discovered yet. Comparison then reports processed/total relative entries, equal files, differing items and the combined file sizes examined on both sides. Mirror and two-way planning have their own processed/total entry progress.
- The exact plan is summarized from its source/destination snapshots before any folder mutation. The confirmation dialog lists files to copy and their total source size, files to delete and their total destination size, and existing versions to overwrite with their old total size. Replacements are included in the copy group and are not counted again as explicit deletions. Directory creation/deletion counts are shown separately, and the operation list includes each file's size. Conflicts remain unchanged and can be expanded for review.
- Both mirror directions and bidirectional synchronization require Apply mirror / Apply synchronization before execution. Cancel, closing the dialog or the absence of a confirmation handler performs no synchronization writes. After acceptance, the preview is revalidated before executing changes.
- During execution the main window keeps separate completed/total file counts and byte totals for copies and deletions, plus completed/planned replacements and directory changes. Counts are committed only after an operation succeeds. A partially transferred temporary file contributes to transferred bytes and current-file progress, but not to completed copies or committed copy bytes. Failed and cancelled runs retain the changes already completed.
- The overall execution bar advances by operations and the fraction of the current copy. A fully transferred file remains below completion until its temporary file has been committed. Per-file and total transferred bytes are displayed separately. Empty files and directory-only operations still advance operation progress.
- Final comparison has its own phase and progress without losing the completed copy/delete summary. Progress snapshots have increasing sequence numbers; delayed UI deliveries cannot replace newer or final state. A new run resets the statistics and ignores deliveries from earlier generations.
- Tests: 220 passed with 99.49% line coverage and 97.84% branch coverage in the documented test scope. Screenshot-only examples can be rendered with `--screenshot <absolute-file.png> --review` or `--progress`; they use synthetic metadata and do not execute filesystem synchronization. See `app/shots/review-statistics.png` and `app/shots/progress-statistics.png` for the checked layouts.

## Per-operation journals and optional HTML reports (2026-11-18)

- Each comparison, mirror or two-way synchronization attempt starts `XtraSync.log` beside the actual executable. The location is derived from `Environment.ProcessPath`, rather than the bundle extraction directory or caller's working directory. On the next operation the existing `XtraSync.bak.log` is removed, the previous current log becomes `XtraSync.bak.log`, and a new current log is created. No dated log archives accumulate, and unrelated files are not removed.
- A temporary exclusive `XtraSync.run.lock` prevents concurrent instances from rotating one another's active log. It is deleted automatically when the session closes. If the journal cannot be started, synchronization does not begin and the reason appears in the status line.
- The UTF-8 journal immediately records the operation, roots, filters, options and phase transitions. Conflicts, ignored cleanup in two-way mode, settings-save warnings, cancellation and operational exceptions are recorded with severity and timestamps. Exceptions include their details and the current path. Each normal completion, cancellation, declined preview, no-change run or handled failure ends with a RUN SUMMARY block, including duration, planned/applied file counts and raw/formatted byte sizes for copies/deletions/replacements, directory counts, transferred bytes (including partial copies), per-phase scanning/comparison totals, equal/different entries, warnings and errors.
- Create HTML report after this run is a main-window option, initially off and not persisted. Its value is captured for one operation and it is cleared in finalization even after failure or cancellation. When selected, a self-contained UTF-8 HTML report with escaped content, inline styling, diagnostics and the same full summary is written beside the executable.
- Reports are named `XtraSync-yyyy-MM-dd-HH-mm-ss.html` using the operation's local start time. Hyphens replace Windows-invalid colon separators. Existing reports are retained; if the same second is reused, `-2`, `-3`, etc. prevent overwriting. The timestamped naming supersedes the earlier proposed single report per day. HTML is written through a newly created temporary sibling and committed when complete. Report-generation errors are logged before the final text summary and shown without claiming that a successful synchronization failed.
- The UI displays the current log and optional HTML paths. Closing the main window during a run requests cancellation and waits for run/report finalization. New runs and restarts cannot rotate the journal until the previous session is disposed.
- Application-owned logs, lock and timestamped HTML files are excluded from scans and preview revalidation only in the actual output directory, independently of user filters. Their containing target directories are protected from mirror deletion and type replacement, including when excluded-target cleanup is enabled. This prevents the live journal from invalidating or destroying itself when the executable folder is inside a synchronized root.
- Validation: 252 tests passed, with 98.67% line coverage and 97.30% branch coverage in the documented scope. The log/report tests use isolated output directories; they cover rotation, collisions, HTML escaping, report failure, partial work, cancellation, preference reset, and source/destination trees containing the active outputs.

## Classified execution failures (2026-11-18)

The full implemented policy is in `ERROR-POLICY.md`. It supersedes the earlier stop-on-first-I/O-error behavior. Transient isolated failures have two retries (250 ms and 750 ms); permanent item failures and failed dependencies are recorded while independent work continues. Unreadable scan scopes are explicitly unavailable, never empty. Known network/device availability failures suspend writes for a bounded recovery check and then require a fresh comparison before resuming, even if access returns. Storage, safety, unexpected runtime failures and unreviewed metadata changes stop the run.

The live UI and both report formats now distinguish failed, skipped, already absent, unavailable, retried, recovered and unprocessed work in addition to committed changes. Completion can be clean, with warnings, or with issues; a blocking failure/cancellation is stopped. Each diagnostic carries category, outcome, phase, operation, path, entry type and exception details. Successful changes remain nontransactional and are retained on partial failure.

Source and destination health are checked before mutations and copy commits. The destination is checked again just before replacement. A directory-to-file conversion prepares the source file before any deletion of its target subtree. Cleanup failures are warnings, and reserved temporary siblings are protected from future synchronization. Root recovery uses a 30-second application budget with two-second checks; native OS calls can take longer and cannot be forcibly cancelled by this loop.

Validation: all 323 tests passed, none failed or skipped; measured coverage was 99.03% of lines and 95.78% of branches in the documented scope. The existing 98%/95% gates passed. The filesystem fault tests cover lost roots, cached transport failures, partial enumeration, access/locking errors, failed prerequisites, missing targets, storage failures, source/target changes and temporary cleanup without modifying real drive availability.

## Cumulative file-read failures (2026-11-18)

The execution policy now also stops on recurrent nonfatal read I/O faults across distinct files, including errors that were later recovered by retries. The main-screen I/O read-error limit defaults to 10 files and is captured per run. Its counter is owned by the run's OperationControl, is never reset by successful files or phase changes, and is reset for the next run. Absolute paths are counted once without case sensitivity. The counter covers payload open/read and known file-metadata reads; missing files, permission/sharing failures and output writes retain their separate classifications. Serious storage and connection failures still stop earlier under the existing policy.

At the limit the current attempt raises ReadErrorLimit, no additional retries/files start, and the normal stop/finalization path records the count, limit, triggering file, exception and prior committed work in the UI, journal and optional HTML report. Source-copy temporary siblings are cleaned up, and a replacement preparation that reaches the limit does not delete its old target subtree. Full policy details and scope are in ERROR-POLICY.md.

Validation: all 362 tests passed, none failed or skipped; coverage reached 99.06% of lines and 96.67% of branches. Tests cover recovered failures, distinct-path accounting across phases, payload read failures, standalone scan/compare/plan calls, preserved targets, counter reset, invalid limits, and stopped-run log/HTML summaries. The 98%/95% coverage gates passed.

## Embedded bilingual user help (2026-11-18)

The standalone offline user guide lives in docs/XtraSync-help.html and is embedded as XtraSync.UserHelp.html in the application assembly. It includes complete French and English sections, an accessible language selector, matching navigation, keyboard focus indicators, responsive dark styling and a print layout. Both languages remain readable without JavaScript. The guide documents modes, metadata comparison, exclusions, review totals, controls, statistics, error handling, log rotation, reports, settings, safety boundaries and troubleshooting.

HelpDocument resolves the real executable directory through Environment.ProcessPath. Every Help command creates a fresh temporary sibling, copies the embedded resource bytes and unconditionally replaces XtraSync-help.html beside the executable before opening it through the default browser. It does not skip identical files or use an old disk copy after a write failure. The temporary is removed on failure. Help remains available during runs; write/browser errors appear under the application title without changing the run status. RunLogStore treats the generated help as a protected runtime artifact only in the actual executable output directory.

Validation: all 370 tests passed with 99.02% line and 96.22% branch coverage; both coverage gates passed. New tests verify exact resource bytes, unchanged/edited/deleted copies, write failure without stale opening, cleanup, shell errors, active-run availability, language/navigation parity and help protection during real fixture synchronization. Local headless Chrome checks passed for FR/EN switching, corresponding section links, valid anchors, dark styling, desktop/mobile widths, no-JavaScript fallback and absence of script errors. Browser shell association itself remains a platform integration boundary.
