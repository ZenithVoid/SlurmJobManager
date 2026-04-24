# SlurmJobManager

A Windows WPF desktop application for submitting, monitoring, and debugging Slurm HPC jobs over SSH.

---

## v3 — What's new

| Area | Capability |
|------|-----------|
| **Theme switching** | Runtime light/dark toggle (Catppuccin-inspired palettes); all controls update instantly |
| **Unified styles** | Shared ResourceDictionary for Button, TextBox, DataGrid, TabItem — no more inline hex colours |
| **Smooth animations** | 180 ms fade-in on panel load; RUNNING jobs pulse softly in the monitor |
| **Log viewer — Follow mode** | Tail-like auto-poll that appends new lines; configurable interval |
| **Log viewer — Search** | Instant in-buffer keyword filter across all cached chunks |
| **Log viewer — Chunk cache** | Bounded 20-chunk sliding window; oldest chunks evicted automatically; cache info shown |
| **Log viewer — Range display** | `Showing 2201–2400 / ~1,200,000 lines` — always visible |
| **Monitor — Filter** | Dropdown: All / PENDING / RUNNING / COMPLETED / FAILED / CANCELLED |
| **Monitor — Keyword search** | Real-time filter by Job ID or job name |
| **Console — History ×50** | Up/Down through last 50 commands |
| **Console — Rich output** | Stdout (neutral), stderr (red), command prompt (green), meta/exit (yellow) |
| **Console — Exec info** | Each command shows `[exit 0 | 123 ms]` after completion |
| **Console — Copy** | One-click copy all output to clipboard |

---

## v2 — Previously

| Area | Capability |
|------|-----------|
| **SSH Connection** | Connect/disconnect/test via password or private key (PEM/PPK); status indicator in title bar |
| **Task Editor** | Root dir browser, auto-generate Task ID, save/load `task.json`, full lifecycle directory scaffold |
| **Parameter Templates** | Browse local template directory, edit template content, save to `params/` with timestamp |
| **sbatch Submission** | Render `{{KEY}}` template variables, upload script, run `sbatch`, parse Job ID, persist to `task.json` |
| **Job Monitor** | `squeue` polling with configurable interval (2–10 s), state-coloured rows, cancel selected job |
| **Log Viewer** | Chunked `.out`/`.err` viewer via `wc -l` + `sed`; configurable chunk size; paging controls |
| **Console Panel** | Embedded remote command console with ↑/↓ history, auto-scroll, stdin-less execution |

---

## UI/UX Features

### Theme switching
Click **☀ Light** / **🌙 Dark** in the title bar to swap the application colour palette instantly.
The two themes are defined in:
- `src/SlurmJobManager.App/Themes/Dark.xaml`  — Catppuccin Mocha-inspired dark palette
- `src/SlurmJobManager.App/Themes/Light.xaml` — Catppuccin Latte-inspired light palette

All brushes are dynamic resources — adding a third theme requires only a new XAML file and a URI change.

### Animations
- **Panel fade-in**: Left, Centre, and Right panels each fade in on load (180 ms, opacity 0→1).
- **Running job pulse**: Rows with state `RUNNING` subtly pulse between 100 % and 70 % opacity (1.5 s, auto-reverse).  Both animations run entirely on the WPF compositor and do not touch the UI thread.

### Virtualised log list
The log `ListBox` uses `VirtualizingStackPanel` with `VirtualizationMode=Recycling` and `ScrollUnit=Item`.  Only the visible rows are materialised, so even a 1 M-line buffer scrolls smoothly.

---

## Large-Log Mode

### How chunked loading works
The log viewer never downloads a whole file.  Instead it uses `wc -l` + `sed` to slice the file by line number on the remote side and streams only the requested window.

### Follow mode (tail-like)
Enable the **Follow mode** checkbox.  Every N seconds (configurable via the interval slider) the viewer polls for lines *after* the current `EndLine`.  If new lines arrive they are appended and the list auto-scrolls to the bottom.

### Chunk cache
| Parameter | Default | Notes |
|-----------|---------|-------|
| Lines/chunk | 200 | Adjustable via slider (50–1000) |
| Max cached chunks | 20 | Oldest chunk evicted automatically when limit is exceeded |
| Effective memory window | 200 × 20 = 4 000 lines | ≈ 400 KB for typical log lines |

The status bar shows e.g. `Cache: 12/20 chunk(s)` and `Showing 2201–2400 / ~1,200,000 lines`.

### In-buffer search
Type in the **🔍** search box to filter the currently cached lines.  The status shows `Search: 47/4000 match(es)`.  This is scoped to loaded chunks only — full remote grep is a planned feature.

---

## Performance Recommendations

| Setting | Recommended | Notes |
|---------|-------------|-------|
| Poll interval | 3–5 s | Lower values increase SSH load |
| Lines/chunk | 100–200 | 200 is a good default for log files with long lines |
| Max cache chunks | 20 (fixed) | Keeps memory bounded; decrease for very long log lines |
| Follow interval | 3–5 s | Match your job's stdout flush rate |

---

## Screenshots

> _(Screenshots below are representative placeholders — capture from a live run for exact visuals.)_

### Task editor & connection panel
```
[Connection fields] [Task setup] [Parameter templates] [Submit]
```

### Job monitor with filter and search
```
Filter: [RUNNING ▾]   🔍 [mysim]
┌──────┬───────────┬──────┬─────────┬───────────┬──────────┬────────┐
│ Job  │ Name      │ User │ State   │ Partition │ Run Time │ Nodes  │
│ 1234 │ mysim_001 │ bob  │ RUNNING │ gpu       │ 00:04:12 │ node01 │
└──────┴───────────┴──────┴─────────┴───────────┴──────────┴────────┘
```

### Log viewer with follow mode and search
```
Remote File Path: /home/bob/jobs/1234/job.out
Lines/chunk: [200]   [▲ Older] [⟳ Latest] [▼ Newer] [🗑 Cache]
☑ Follow mode (tail)   Interval: 3s
🔍 [error]
──────────────────────────────────────────────────────────────────────
line 2198: Iteration 99/100 complete
line 2199: [error] convergence warning at step 99
...
Showing 2001–2400 / ~1,200,000 lines   Cache: 2/20 chunk(s)
Search: 1/400 match(es)
```

---

## Architecture

```
src/
├── SlurmJobManager.Core/
│   ├── Interfaces/
│   ├── Models/
│   └── Services/
├── SlurmJobManager.Infrastructure/
│   ├── Logs/
│   ├── Ssh/
│   └── Storage/
└── SlurmJobManager.App/
    ├── Themes/
    │   ├── Dark.xaml             # NEW: dark colour palette
    │   └── Light.xaml            # NEW: light colour palette
    ├── Styles/
    │   └── Controls.xaml         # NEW: unified control styles
    ├── Converters/
    │   └── Converters.cs         # NEW: JobStateToBrush, BoolToVisibility, ConsoleLineKindToBrush
    ├── ViewModels/
    │   ├── ConsoleLine.cs        # NEW: typed console output line
    │   ├── ConsoleViewModel.cs   # UPDATED: 50-entry history, exec-time, stderr colouring, copy
    │   ├── LogViewerViewModel.cs # UPDATED: follow mode, search, chunk cache pruning
    │   ├── MainViewModel.cs      # UPDATED: theme toggle
    │   ├── MonitorViewModel.cs   # UPDATED: status filter, keyword search
    │   └── ...
    └── Views/
        ├── ConnectionView.xaml   # UPDATED: themed styles
        ├── ConsoleView.xaml      # UPDATED: rich output template, copy button
        ├── LogViewerView.xaml    # UPDATED: follow toggle, search bar, cache info
        ├── MonitorView.xaml      # UPDATED: filter/search controls, state converter
        ├── TaskEditorView.xaml   # UPDATED: themed styles
        └── MainWindow.xaml       # UPDATED: theme toggle button, fade-in animations
```

---

## Requirements

- **Windows** (WPF app, `net8.0-windows`)
- **.NET 8 SDK**
- SSH access to a Slurm cluster

---

## Build & run

```bash
dotnet restore SlurmJobManager.sln
dotnet build   SlurmJobManager.sln
dotnet run --project src/SlurmJobManager.App
```

---

## Minimal workflow

1. **Connect** — fill in Host / Port / Username / Password (or Private Key) → click **Connect**
2. **Create task** — set Root Directory + click **New** for a Task ID → **Save Task**
3. **Edit parameter file** — point to a template directory, pick a file, edit, **Save Param File**
4. **Submit** — set Remote Work Directory + App Path → **Submit sbatch Job** → Job ID appears
5. **Monitor** — type your cluster username → **▶ Poll** to start watching `squeue` output
6. **View logs** — paste the remote `.out` / `.err` path → **⟳ Latest** → enable **Follow mode**

---

## Local task directory layout

```
{RootDirectory}/{TaskId}/
├── task.json
├── params/
├── scripts/
│   └── submit.sbatch
├── logs/
│   └── submit.log
└── result-cache/
```

---

## sbatch template variables

| Variable | Value |
|----------|-------|
| `JOB_NAME` | Task ID |
| `WORK_DIR` | Remote work directory |
| `APP_PATH` | Application path on remote |
| `PARAM_FILE` | Full path to selected parameter file on remote |
| `STDOUT_FILE` | `{WORK_DIR}/logs/job.out` |
| `STDERR_FILE` | `{WORK_DIR}/logs/job.err` |

Extra rows in the **Extra Parameters** grid are substituted too.

---

## Known limitations

- `PasswordBox` contents are held in memory only — no credential vault
- Log search is scoped to loaded chunks only; full remote grep is planned
- `squeue` parser covers basic fields; heterogeneous cluster output may need adjustment
- SSH private key path is stored in plaintext app state (not encrypted)

---

## Phase 4 roadmap

- DPAPI / Windows Credential Manager for secure credential storage
- Remote full-text grep (server-side `grep` piped through SSH)
- `sacct` integration for completed job history and accounting data
- Per-cluster profile theming/presets
- Telemetry hooks for performance diagnostics

