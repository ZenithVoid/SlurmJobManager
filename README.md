# SlurmJobManager

Windows WPF application for submitting and monitoring Slurm jobs on a remote CentOS7 cluster via SSH.

## Features (skeleton)

- SSH connection profile management (host/user/password or key path)
- Task root directory management: `Root/{TaskId}`
- Parameter template selection and save-to-task workflow
- sbatch script generation from a base template
- Submit job, parse returned Slurm Job ID
- Monitor all jobs for a specified user (`squeue`/`sacct`)
- View `.out`/`.err` logs with chunked paging (large-log friendly)
- Embedded command console panel (remote command execution)

## Tech stack

- .NET 8
- WPF (MVVM)
- SSH.NET

## Project structure

- `src/SlurmJobManager.App` - WPF UI app
- `src/SlurmJobManager.Core` - domain models, interfaces, business logic
- `src/SlurmJobManager.Infrastructure` - SSH + file system implementations

## Notes

This is an initial scaffold meant to be extended with your actual sbatch template and cluster-specific command options.
