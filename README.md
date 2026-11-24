# XtraSync

XtraSync is a Windows desktop application for comparing two folders, mirroring one folder to another, or synchronizing files in both directions. It shows the planned copies, replacements, and deletions before asking you to apply them.

[Download XtraSync 1.0.2 for Windows x64](https://github.com/odahan/XtraSync/releases/tag/V102)

Download `XtraSync.exe` and run it from a writable folder. The executable includes the .NET runtime and needs no installer. XtraSync writes its logs, optional reports, and generated help beside the executable.

## Choose an operation

| Command | Behavior |
| --- | --- |
| **Compare folders** | Lists differences using file size and modification time. Leaves the selected folders' data untouched. |
| **Mirror to right →** | Makes the right folder match the left within the selected scope. The left folder is the source. |
| **← Mirror to left** | Makes the left folder match the right within the selected scope. The right folder is the source. |
| **Synchronize Both Way** | Copies missing files and identifiable newer versions in either direction. Preserves unresolved conflicts and does not propagate deletions. |

Start with **Compare folders**, then review the direction, file counts, and sizes before applying a mirror or synchronization. Mirror deletions are permanent, and completed changes are retained if a run stops early.

## Filters, progress, and reports

- Name and relative-path filters apply during scanning. Excluded items are preserved in a mirror destination by default. An explicit mirror option lets you include them in the deletion preview. Two-way synchronization ignores exclusions on both sides.
- Live counters show progress, copied and deleted files, byte totals, and problems. You can pause, resume, stop, or restart a run.
- The log records the outcome and diagnostics. XtraSync keeps the current log and one previous log. You can request a timestamped HTML report before starting a run.
- Repeated read errors stop the run at a configurable limit of distinct files, initially ten. Recovered reads count toward that limit.

Click **Help** for the offline English and French guide. The button refreshes the local guide from the executable before opening it in your browser. The source guide is available in [docs](docs/XtraSync-help.html). Download or save the HTML file to open it as a page.

## Build and test

On Windows with the .NET 11 SDK installed, run these commands in PowerShell:

```powershell
dotnet build .\XtraSync.slnx -m:1
.\Test.ps1
.\Publish.ps1
```

`Test.ps1` runs the suite and checks coverage thresholds. `Publish.ps1` produces the self-contained Windows x64 executable at `Publish/XtraSync.exe`. You can also run `Publish.bat` from Explorer. The production executable is distributed through GitHub Releases.

See the [test suite](tests/README.md), [execution error policy](ERROR-POLICY.md), and [credits](CREDITS.md) for details.

## License and donations

XtraSync is open source under the [MIT License](LICENSE). Donations are entirely optional and every feature is available free of charge.

If XtraSync is useful to you, you can support its development through [Buy Me a Coffee](https://buymeacoffee.com/knhsynths).
