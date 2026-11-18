# XtraSync tests

## Run

Requires Windows and the .NET 11 SDK. From the project root:

```powershell
.\Test.ps1
```

The script builds the solution, runs every test, collects coverage and fails if line coverage is below 98% or branch coverage is below 95%. It resolves all paths from its own location and can also be called by absolute path from another working directory. The latest report is copied to `TestResults/coverage.cobertura.xml`; each run also retains its own report directory.

Run tests without coverage:

```powershell
dotnet test XtraSync.slnx -c Release -m:1 --disable-build-servers
```

Run coverage directly:

```powershell
dotnet test XtraSync.slnx -c Release -m:1 --disable-build-servers --collect:"XPlat Code Coverage" --settings tests/coverage.runsettings --results-directory TestResults
```

`-m:1` is required for every `dotnet build` and `dotnet test` invocation. Tests use xUnit with the VSTest runner and the [Coverlet collector](https://github.com/coverlet-coverage/coverlet/blob/master/Documentation/VSTestIntegration.md).

## Scope

| Suite | Behaviors |
| --- | --- |
| `ComparisonRulesTests` | Unknown FILETIMEs, second truncation, inclusive tolerance, order, explicit daylight shift, time-zone rounding and 24-hour cutoff, incomplete date/time flags. |
| `ExclusionListTests` | Case-insensitive names and paths, both separators, wildcards restricted to a component, literal special characters, whitespace/newlines, save/overwrite/clear, malformed and inaccessible JSON, failed replacement cleanup. |
| `FolderHistoryTests` | Independent left/right history, normalization and deduplication, move-to-front and ten-item limit, invalid/missing paths, JSON round trips and filtering, unavailable files, failed replacement cleanup. |
| `OperationControlTests` | Pause/resume, cancellation of paused and future checkpoints, phase resets, counts and bytes, optional callbacks, deterministic 80 ms throttling, binary size units through TB. |
| `FolderServiceTests` | Root validation, recursive and shallow scans, empty directories, progress, status precedence, case-insensitive sorted comparison, mirror directions, two-way sync, type and ambiguous conflicts, exclusions and protected ancestors, stale plans, timestamp preservation, copy failure and cancellation cleanup. |
| `LinkedFolderTests` | Junction roots and ancestors, linked scan entries, scope/exclusion handling, links injected after preview validation at source/destination items and parents. |
| `MainViewModelTests` | Defaults and injected persistence, folder selection/cancellation, settings errors, property and command notifications, options and validation, comparison, all synchronization commands, required confirmation, conflicts, stale previews, stop/resume/restart, stale progress generations. |
| `FilterSettingsTests` | Default cleanup off, legacy settings migration, typed settings persistence, detached dialog draft, clear/cancel/save, summary and command notifications, passing filters to every plan mode. |
| `UpstreamFilterTests` | Type-specific patterns, upstream source omission, excluded locked files, optional target deletion in either mirror direction, both-way ignore semantics, shallow scope, protected ancestors, type replacement and preview revalidation policies. |
| `ExclusionLineTests` | Main-line parsing/serialization, one-item-per-line dialog drafts, typed round trips, manual text as the source of truth, independent cleanup preference, immediate saving without recording keystrokes, ten recent lines, duplicate promotion, malformed history, legacy migration and applying typed manual lines to operations. |
| `OperationStatisticsTests` | Exact copy/delete/replacement byte totals in either direction, two-way summaries, type conflicts, excluded-target cleanup, zero-byte files, read-free confirmation, partial failure and cancellation accounting, comparison totals, within-file progress, confirmation before writes, final totals after rescan and stale progress rejection. |
| `RunLogTests` | Three-run rotation, one-backup retention, concurrent writer protection, lock cleanup, complete text summaries and diagnostics, timestamped HTML, same-second collisions, invariant filename dates, escaped self-contained HTML, report failures and exact output-artifact matching. |
| `RunLoggingIntegrationTests` | Journals for each operation, one-shot report reset on success/failure/decline/stop/no changes, planned versus applied totals, conflicts and settings warnings, partial failures, unwritable destinations, shutdown waiting and safe synchronization when logs live inside a selected root. |
| `ErrorPolicyTests` | Windows error classification, bounded item retries, permanent/fatal errors, root timeouts and reconnection, transport errors with cached metadata, cancellation of waits, diagnostic categories and unique outcome counters. |
| `FailureHandlingTests` | Real fixture operations with injected faults, continuing independent copies, already-absent targets, failed prerequisites, pre-staged type replacements, unknown/partially scanned scopes, changed data before commit, fatal storage and temporary cleanup. |
| `FailurePolicyIntegrationTests` | Final completed-with-issues/warnings/stopped statuses, log and HTML results, unknown scopes in confirmation, root loss before deletion and destination edits during a copy. |
| `ReadErrorBudgetTests` | Ten-file cumulative read-error stop, same-file retry deduplication, successful reads between failures, metadata/content read faults, in-stream failures, write-error separation, protected type conversion, standalone APIs, per-run reset, configured limits and complete journal/HTML results. |
| `HelpDocumentTests` | Exact embedded bilingual guide extraction, unconditional replacement on every click, missing/edited/unchanged files, failed writes without stale opening, temporary cleanup, shell failures, help during an active run, navigation/language parity and synchronization protection. Browser launching and HTML rendering also require integration verification. |

Filesystem tests are integration tests alongside pure unit tests: they exercise real Windows filesystem behavior in disposable GUID-named directories under the test output directory. No test reads or writes the user's `%LOCALAPPDATA%/XtraSync` settings. Junction tests use `mklink /J`, which does not require symbolic-link privileges. Synchronization and progress tests use explicit signals or a manual clock rather than timing file copies against the speed of the host.

## Coverage boundaries

Validation after the error-policy implementation is recorded in `MECHANICS.md`. The root-level `Test.ps1` enforces the existing coverage thresholds. Tests inject log destinations under disposable fixtures and use controlled filesystem failures and a virtual clock for outage tests; they never disconnect real drives or write journals beside the production executable. See `ERROR-POLICY.md` for the implemented decisions and their limits.

Coverage includes the application's comparison rules, stores, synchronization service, operation controls, statistics, formatter and application ViewModels, including async continuations and progress callbacks. Generated MVVM accessors and `obj/` sources are excluded. WPF startup (`App`), native window/dialog code (`MainWindow`, `FilterDialog`, `ReviewDialog`, `DarkChrome`) and screenshot rendering (`ScreenshotCapture`) are excluded explicitly: they need UI/integration testing, which this suite does not claim to provide. Shell publishing scripts are also outside managed-code unit coverage.

The default ViewModel constructor delegates to the two real user stores and is deliberately not invoked by isolated tests. Some defensive branches cannot be reached through valid application plans or timestamp inputs. Report percentages describe the stated scope rather than the entire UI or every theoretical race with external filesystem changes.

## Environment

The CLI may need `DOTNET_CLI_TELEMETRY_OPTOUT=1` if a restricted host denies telemetry registry access. The test runner and collector need local process communication; this environment required execution outside its restricted sandbox. NU1900 means NuGet could not retrieve vulnerability metadata, and is not suppressed by this setup.
