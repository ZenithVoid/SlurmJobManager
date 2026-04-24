# SlurmJobManager

A Windows WPF desktop application for submitting, monitoring, and debugging Slurm HPC jobs over SSH.

---

## v2 — What's new

| Area | Capability |
|------|-----------|
| **SSH Connection** | Connect/disconnect/test via password or private key (PEM/PPK); status indicator in title bar |
| **Task Editor** | Root dir browser, auto-generate Task ID, save/load `task.json`, full lifecycle directory scaffold |
| **Parameter Templates** | Browse local template directory, edit template content, save to `params/` with timestamp |
| **sbatch Submission** | Render `{{KEY}}` template variables, upload script, run `sbatch`, parse Job ID, persist to `task.json` |
| **Job Monitor** | `squeue` polling with configurable interval (2–10 s), state-coloured rows, cancel selected job |
| **Log Viewer** | Chunked `.out`/`.err` viewer via `wc -l` + `sed` — never loads the full file; configurable chunk size; paging controls |
| **Console Panel** | Embedded remote command console with 20-entry ↑/↓ history, auto-scroll, stdin-less execution |

---

## Architecture

```
src/
├── SlurmJobManager.Core/              # Domain models + interfaces + SbatchTemplateRenderer
│   ├── Interfaces/
│   │   ├── ILogChunkService.cs
│   │   ├── ISlurmService.cs
│   │   ├── ISshClientService.cs
│   │   └── ITaskStorageService.cs
│   ├── Models/
│   │   ├── ConnectionProfile.cs
│   │   ├── LogChunkRequest.cs
│   │   ├── LogChunkResult.cs
│   │   ├── SlurmJobStatus.cs
│   │   └── TaskRecord.cs
│   └── Services/
│       └── SbatchTemplateRenderer.cs
├── SlurmJobManager.Infrastructure/    # SSH.NET-backed implementations
│   ├── Logs/
│   │   └── SshLogChunkService.cs      # NEW: wc -l + sed chunked log access
│   ├── Ssh/
│   │   ├── SlurmService.cs
│   │   └── SshClientService.cs
│   └── Storage/
│       └── TaskStorageService.cs
└── SlurmJobManager.App/               # WPF (MVVM, .NET 8)
    ├── ViewModels/
    │   ├── ConnectionViewModel.cs     # NEW
    │   ├── ConsoleViewModel.cs        # NEW
    │   ├── LogViewerViewModel.cs      # wired
    │   ├── MainViewModel.cs           # wired (DI constructor)
    │   ├── MonitorViewModel.cs        # wired
    │   ├── TaskEditorViewModel.cs     # wired
    │   └── ViewModelBase.cs           # + AsyncRelayCommand, RelayCommand<T>
    └── Views/
        ├── ConnectionView.xaml        # NEW
        ├── ConsoleView.xaml           # NEW
        ├── LogViewerView.xaml
        ├── MonitorView.xaml
        └── TaskEditorView.xaml
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
6. **View logs** — paste the remote `.out` / `.err` path → **⟳ Latest** to load the tail

---

## Local task directory layout

```
{RootDirectory}/{TaskId}/
├── task.json          # task metadata, last job ID, parameter dict
├── params/            # edited parameter files (saved from template editor)
├── scripts/
│   └── submit.sbatch  # rendered sbatch script
├── logs/
│   └── submit.log     # submission timestamp + job ID
└── result-cache/      # reserved for future result downloads
```

---

## sbatch template variables

The built-in template supports these `{{KEY}}` placeholders:

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

## Known limitations (v2)

- `PasswordBox` contents are held in memory only — no credential vault
- `squeue` parser covers basic fields; heterogeneous cluster output may need adjustment
- No auto-follow mode for running job logs
- SSH private key path is stored in plaintext app state (not encrypted)

---

## Phase 3 roadmap

- DPAPI / Windows Credential Manager for secure credential storage
- Log virtualization with auto-follow for running jobs
- `sacct` integration for completed job history and accounting data
- Rich sbatch preset library
- Retry / timeout / cancellation policies throughout

