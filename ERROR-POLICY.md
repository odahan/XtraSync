# XtraSync execution error policy

This policy applies to comparisons, mirror planning, preview revalidation and filesystem execution. The same decisions apply in both mirror directions and in two-way synchronization.

## Decisions

| Condition | Decision |
| --- | --- |
| A configured root does not exist or cannot establish safe access at startup | Stop before synchronization writes. Report both configured paths and the reason. |
| A file is temporarily locked or has another isolated transient I/O failure while both roots remain accessible | Retry twice, after 250 ms and 750 ms. A successful retry is reported as recovered; otherwise mark the operation failed and continue independent work. |
| Nonfatal I/O read errors recur on distinct files, even when retries eventually recover them | Accumulate distinct absolute file paths across the entire run. Stop immediately at the configured limit, initially **10 files**. Successful intervening reads, phase changes and retries do not reset this counter. |
| Permanent access denial, invalid/too-long path, or a directory that cannot be deleted because its contents changed | Record a per-item failure without retrying, preserve remaining data and continue independent work. |
| A source file disappeared after preview | Fail its copy, preserve its existing destination, and continue independent work. Never turn that disappearance into an unreviewed mirror deletion. |
| A target scheduled for deletion is already absent | Record “already absent.” Do not add it or its planned bytes to successful deletions. |
| A directory or entry cannot be fully scanned | Mark the scope unavailable. Comparison displays `Unavailable`, and synchronization blocks changes in that scope and destructive changes to its ancestors. An incomplete directory listing is discarded rather than treated as complete. |
| An operation's prerequisite failed | Skip the dependent operations without attempting them. For example, a failed child deletion blocks removal or replacement of its parent, and a failed folder creation blocks descendant copies. Independent paths continue. |
| A root disconnects or Windows reports a transport/device-unavailable error | Suspend writes and check availability at two-second intervals, with a 30-second recovery budget. Stop if it stays unavailable. Even if it reconnects, stop and require a fresh comparison/preview before further writes. Cached metadata alone cannot justify continuing after a transport error. |
| Disk full, write-protected/faulty storage, CRC, device I/O or filesystem corruption | Stop immediately. Do not treat the error as an isolated file to skip. |
| An unreviewed addition, type change or file size/timestamp change is discovered | Stop and require a fresh comparison and confirmation. Missing items are handled conservatively as described above. |
| A reparse point appears, roots overlap, or another path-safety invariant is violated | Stop. No retry or automatic bypass. |
| An unexpected programming/runtime exception occurs | Stop and log the full exception. It is not silently classified as a skippable item. |

The Windows error mapping follows Microsoft's [codes 0–499](https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--0-499-), [codes 1000–1299](https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--1000-1299-) and [codes 1300–1699](https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--1300-1699-). Defaults are defined in `app/ErrorPolicy.cs`; tests can inject a filesystem, clock and wait implementation without changing production defaults.

## Cumulative read-error limit

The main screen's **I/O read-error limit** field defaults to 10 distinct files and accepts a positive whole number. The selected value is captured for the run, included in the journal options and retained for subsequent runs in the current application session. A new run creates a fresh counter. Editing the next run's limit does not relabel the retained statistics of the previous run.

The counter covers nonfatal I/O faults while opening/reading file contents or reading known file metadata on either side. The same absolute path counts once, ignoring case, even if it fails repeatedly or fails in multiple phases. A read that succeeds after a retry remains counted. Reaching the limit interrupts the current attempt before further retries and prevents later operations from starting. It produces the `ReadErrorLimit` fatal category, a clear final message, per-file warning diagnostics and the normal text/optional HTML summary. Already committed changes are retained; the current copy's temporary file is cleaned up.

Missing files, access denial, sharing/locking violations, invalid paths and write/commit errors do not consume this read-error budget: they keep their separate policies. Known serious storage and transport failures retain their earlier immediate-stop/recovery behavior and do not wait for ten files. Directory listing failures remain unknown scopes rather than fabricated missing entries. The safeguard is intentionally cumulative, not a consecutive-failure streak or a reset-on-success counter.

## Protections during execution

- Both roots are checked before each mutation attempt and again before committing a copied file. After a detected outage, remaining writes require a new reviewed plan.
- Preview revalidation and per-action checks reject changed metadata and unexpected destination entries. A copy checks the source's size/timestamp and the byte count, then checks the destination again immediately before replacement.
- Copies use temporary siblings and replace destinations only after a complete, validated copy. Partial/retried transfer bytes remain visible but are not counted as committed copies.
- Before deleting a target directory to replace it with a source file, the source file is fully prepared in a temporary sibling. If preparation fails, deletion of that target subtree is blocked.
- Temporary cleanup is attempted on failure/cancellation and at the end of execution. A cleanup failure is logged as a warning. Reserved `.xtrasync-<32 hexadecimal characters>.tmp` files are never scanned/copied as user data; their parent directories are protected from mirror deletion.
- The approved action list is never enlarged after confirmation. Newly discovered missing or unavailable entries cannot generate new deletions.

Synchronization remains nontransactional: successful changes, including explicit deletions in reviewed type conversions, are not rolled back when a later operation fails. External filesystem changes can still occur between checks and OS calls. The 30-second budget bounds the application's recovery loop **between completed filesystem calls**; it cannot forcibly interrupt a Windows filesystem call that is itself blocked. Stop cancels retry waits immediately and takes effect at the next checkpoint when a native call returns.

## Results, counters and reports

- **Completed**: all attempted work succeeded, or reviewed deletion targets were already absent.
- **Completed with warnings**: transient failures recovered or configuration/output warnings occurred. The final status directs the user to the log.
- **Completed with issues**: some items failed, were skipped by dependency, remained unavailable, or had unresolved conflicts. The final status gives counts and directs the user to the log.
- **Stopped**: user cancellation or a blocking/fatal condition interrupted the run. Prior successful changes are retained and pending operations are not reported as completed.

Live counters, `XtraSync.log` and optional HTML reports distinguish:

- planned versus committed copies, deletions, replacements and directory operations;
- failed files/folders and known failed-file bytes;
- missing source files and inaccessible/locked files;
- dependency-skipped files/folders and known skipped bytes;
- already-absent files/folders and their planned bytes, excluded from actual deleted bytes;
- unique unavailable scan scopes, retries, recovered operations and conflicts;
- distinct files with nonfatal I/O read errors, including those recovered by retries;
- file and folder operations not processed after an interruption.

Diagnostics identify the phase, action, path, known entry type, category, outcome and exception details. Repeated scans of the same unavailable scope do not inflate its counter, while every diagnostic remains in the log. Counters reset for a new run and persist across the phases of that run. The existing log rotation, HTML naming and one-shot report option apply to all result types.
