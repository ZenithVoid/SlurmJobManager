# SlurmJobManager

Windows WPF application for submitting and monitoring Slurm jobs on a remote CentOS7 cluster via SSH.

## Overview

SlurmJobManager runs on Windows and connects to a CentOS 7 Slurm controller node (e.g. `mu`) via SSH.
It lets you:

- Define a **Task Root Directory** and per-task sub-directories (`Root/{TaskId}/`)
- Select, edit and persist **sbatch parameter templates**
- **Submit** sbatch scripts and track their Slurm Job ID
- **Monitor** all jobs for a given user in real-time
- Browse huge `.out`/`.err` log files with **chunked paging** (never loads the full file)
- Execute arbitrary remote commands in an **embedded console panel**

## Implemented (skeleton v1)

| Area | What's in place |
|------|----------------|
| **Core** | Domain models: `ConnectionProfile`, `TaskRecord`, `SlurmJobStatus`, `LogChunkRequest/Result` |
| **Core** | Interfaces: `ISshClientService`, `ISlurmService`, `ITaskStorageService`, `ILogChunkService` |
| **Core** | `SbatchTemplateRenderer` – `{{KEY}}` token substitution + placeholder extraction |
| **Infrastructure** | `SshClientService` – password & private-key auth via SSH.NET |
| **Infrastructure** | `SlurmService` – `sbatch` submit (parses job ID), `squeue` polling, `scancel` |
| **Infrastructure** | `TaskStorageService` – `Root/{TaskId}/task.json` save/load/list |
| **Infrastructure** | `SshLogChunkService` – `wc -l` + `sed -n` chunked log reading |
| **App (WPF)** | `MainWindow` with three resizable panels (Task Editor / Monitor / Log Viewer) |
| **App (WPF)** | `TaskEditorViewModel` – root dir, task ID, template, parameter grid, submit |
| **App (WPF)** | `MonitorViewModel` – user job list, refresh / start-stop polling, cancel |
| **App (WPF)** | `LogViewerViewModel` – `LoadLatest`, `LoadOlder`, `LoadNewer` paging commands |

## Tech stack

- .NET 8 / C#
- WPF (MVVM, no third-party MVVM framework)
- [SSH.NET](https://github.com/sshnet/SSH.NET) (`Renci.SshNet`)

## Directory structure

```
SlurmJobManager.sln
src/
  SlurmJobManager.Core/          # Domain models, interfaces, SbatchTemplateRenderer
    Models/
    Interfaces/
    Services/
  SlurmJobManager.Infrastructure/ # SSH.NET-backed implementations
    Ssh/
    Storage/
    Logs/
  SlurmJobManager.App/            # WPF + MVVM shell
    ViewModels/
    Views/
SlurmJobManager/                  # (legacy WinUI scaffold – kept for reference)
```

## Local build & run

```bash
# Restore NuGet packages
dotnet restore SlurmJobManager.sln

# Build all projects
dotnet build SlurmJobManager.sln

# Run the WPF app (Windows only)
dotnet run --project src/SlurmJobManager.App
```

> **Note:** The WPF app (`net8.0-windows`) must be built on Windows or with the Windows SDK available.
> The Core and Infrastructure libraries build on any OS.

## Known limitations (v1 skeleton)

- UI is placeholder-level; no real SSH connection wired up yet
- Slurm `squeue` parser covers basic fields only
- Log chunk paging works but has no UI "follow mode"
- No credential storage / secret protection
- No error/retry policies

## Next steps (phase 2)

- Wire up real SSH connection dialog and `ISshClientService` into the App
- Integrate sbatch template file browser and parameter editor
- Improve `squeue`/`sacct` parser with richer job fields
- Add log virtualization and auto-follow mode for running jobs
- Add credential vault / secure storage
- Add retry, timeout, and cancellation policies throughout
