# SlurmJobManager

A Windows WPF desktop application for submitting, monitoring, and debugging Slurm HPC jobs over SSH.

---

## 发布产物自动生成（zip + latest.json）

可使用脚本将 `dotnet publish` 输出自动打包为标准更新产物：

1. 先发布主程序（示例）：

   `dotnet publish --nologo /home/runner/work/SlurmJobManager/SlurmJobManager/src/SlurmJobManager.App/SlurmJobManager.App.csproj -c Release -r win-x64`

2. 生成更新产物（zip + latest.json）：

   `pwsh /home/runner/work/SlurmJobManager/SlurmJobManager/scripts/Generate-ReleaseArtifacts.ps1 -PublishDirectory <publish-output-dir> -OutputDirectory <release-output-dir> -RuntimeIdentifier win-x64 -Notes "release notes"`

脚本输出目录包含：
- `SlurmJobManager-<version>-<rid>.zip`
- `latest.json`

可选参数：
- `-GenerateLegacyVersionJson`：额外生成兼容文件 `version.json`
- `-PublishedAtUtc`：手动指定发布时间（ISO-8601）

---

## 本次修复说明（UI可用性修复）

### 1. 自定义应用内标题栏

系统原生标题栏已完全移除（`WindowStyle="None"`），取而代之的是应用顶部的自定义标题栏，提供以下功能：

- **右上角三个窗口控制按钮**：最小化（`⊟`）、最大化/还原（`⊡`/`❐`）、关闭（`✕`）
- **拖拽移动窗口**：在标题栏空白区域按住鼠标左键拖动即可移动窗口
- **双击最大化/还原**：双击标题栏空白区域切换最大化状态
- **`WindowChrome`**：保留了 Windows 的窗口贴边 Snap、调整大小等系统行为
- **关闭按钮 hover 红色**：鼠标悬停时关闭按钮变为红色，符合 Windows 11 风格

图标使用 Segoe MDL2 Assets 字体（Windows 内置），无需外部 CDN。

---

### 2. 侧边栏导航修复

原先使用 `SelectedValue` + `SelectedValuePath` 的绑定方式在 WPF 中存在初始化顺序问题，可能导致 Tab 切换失效。现已改为更可靠的 `SelectedItem` + `ActiveNavItem` 双向绑定：

- 新增 `ActiveNavItem` 属性（`NavItem?` 类型），ListBox 的 `SelectedItem` 绑定到该属性
- `ActiveNavItem` 变化时自动更新 `ActiveTab`，`ActiveTab` 变化时同步 `ActiveNavItem`
- 默认首页为 **Dashboard（仪表盘）**
- 全部 6 个 Tab（仪表盘、任务、监控、日志、终端、设置）均可正常切换

---

### 3. 默认中文与多语言结构

采用 **ResourceDictionary 字符串资源**方案，支持多语言：

| 文件 | 语言 |
|------|------|
| `Localization/Strings.zh-CN.xaml` | 中文（简体）— **默认** |
| `Localization/Strings.en-US.xaml` | English (US) |

- 应用启动时**无论系统语言如何**，自动加载中文资源（在 `App.xaml.cs` 的 `OnStartup` 中调用 `ApplyLocale("zh-CN")`）
- UI 文本（导航名称、按钮、标题、状态提示、错误提示）均使用 `DynamicResource` 引用，随语言切换实时更新
- **在"设置"页面**可点击按钮切换中文/英文界面
- 扩展多语言：新增 `Localization/Strings.{locale}.xaml` 文件并在 Settings 页增加对应按钮即可

---

### 4. 平滑滚动设计与限制

通过 `SmoothScrollBehavior`（附加行为，`Behaviors/SmoothScrollBehavior.cs`）实现丝滑滚动：

**工作原理：**
1. 附加到 `ScrollViewer`，拦截 `PreviewMouseWheel` 事件
2. 计算目标偏移量（累积多次滚轮输入，避免"追赶"动画）
3. 使用 `DoubleAnimation` + `CubicEase EaseOut`（180 ms）动画插值到目标位置
4. 通过辅助附加属性 `ScrollViewerHelper.VerticalOffset` 驱动实际滚动（因为原生 `VerticalOffset` 不可动画化）

**已应用范围：**
- 📋 **日志视图**（最高优先级）：外层 ScrollViewer + `SmoothScrollBehavior`，ListBox 设置 `ScrollUnit=Pixel`
- 📊 **监控视图**：DataGrid 外层 ScrollViewer + `SmoothScrollBehavior`
- **全局**：`ScrollViewer` 默认样式中 `CanContentScroll=False`，避免按逻辑项跳动

**已知限制：**
- 日志视图保留了 WPF 虚拟化（`VirtualizingPanel.IsVirtualizing=True`，`ScrollUnit=Pixel`），不会因平滑滚动导致内存暴涨
- `ScrollUnit=Pixel` 与虚拟化配合时，超大列表（百万行）的滚动性能与原始表现持平
- 平滑动画动持续 180 ms，快速连续滚动时目标偏移会累积并追赶，不会产生排队卡顿

---

## v5 — UI/UX Redesign (Sidebar Navigation)

| Area | Capability |
|------|-----------|
| **Sidebar navigation** | Six tabs (Dashboard / Tasks / Monitor / Logs / Console / Settings) replace the old three-panel horizontal layout |
| **Dashboard** | Live connection status, per-state job counters, and quick-action shortcuts |
| **Settings tab** | SSH connection config, theme switch, and polling-interval control all in one place |
| **Custom scrollbars** | Slim (10 px), rounded, theme-aware scrollbars across every scroll region |
| **Layered resource dictionaries** | `Themes/Colors.{Dark,Light}.xaml`, `Themes/Typography.xaml`, `Styles/Buttons.xaml`, `Styles/Inputs.xaml`, `Styles/DataGrid.xaml`, `Styles/ScrollBars.xaml`, `Styles/TabsAndSidebar.xaml` |
| **Card-based layout** | Rounded cards with subtle borders and consistent 8/12/16/24 spacing |
| **Fade transitions** | 180 ms opacity fade-in when switching between tabs |

---

## UI Design & Navigation

### Sidebar information architecture

The application uses a **left sidebar** as the primary navigation surface. Six tabs are always visible:

| # | Tab | Purpose |
|---|-----|---------|
| 1 | 🏠 **Dashboard** | Connection status, job stats summary, quick actions — *default view* |
| 2 | ⚡ **Tasks** | Task root dir, Task ID, app path, parameter templates, sbatch submit |
| 3 | 📊 **Monitor** | Live `squeue` table with filter, search, auto-poll, and job cancel |
| 4 | 📋 **Logs** | Chunked `.out`/`.err` viewer with paging, follow-mode, and search |
| 5 | ⌨ **Console** | Interactive remote command terminal with history and colour output |
| 6 | ⚙ **Settings** | SSH connection config, theme toggle (dark/light), poll interval |

The selected tab is highlighted with an accent-blue left border and a subtle background fill. Tab switching triggers a 180 ms fade-in animation on the content pane.

### Theme & scrollbar customisation

**Themes** are swapped at runtime by replacing the first entry in `Application.Resources.MergedDictionaries`:

| Theme | Root file | Color tokens |
|-------|-----------|--------------|
| Dark (default) | `Themes/Dark.xaml` | `Themes/Colors.Dark.xaml` — Catppuccin Mocha-inspired |
| Light | `Themes/Light.xaml` | `Themes/Colors.Light.xaml` — Catppuccin Latte-inspired |

Typography tokens (font family, sizes, corner radii, spacing) live in `Themes/Typography.xaml` and are merged through `Styles/Controls.xaml`, so they are theme-independent.

**Custom scrollbars** are defined in `Styles/ScrollBars.xaml`:
- Width/height: **10 px**
- Thumb corner radius: **5 px** (fully rounded)
- Default thumb: semi-transparent `ScrollThumbBrush`
- Hover: `ScrollThumbHoverBrush` (darker/brighter)
- Dragging: `ScrollThumbActiveBrush` (accent colour)
- Track: `ScrollTrackBrush` (barely visible)
- All brushes are `DynamicResource` — they switch automatically on theme change
- Virtualization in `ListBox` / `DataGrid` is preserved; custom scrollbars do not affect scrolling performance

### Resource dictionary hierarchy

```
App.xaml
├── Themes/Dark.xaml           ← swapped at runtime for Light.xaml
│   └── Themes/Colors.Dark.xaml
└── Styles/Controls.xaml
    ├── Themes/Typography.xaml
    ├── Styles/Buttons.xaml
    ├── Styles/Inputs.xaml
    ├── Styles/DataGrid.xaml
    ├── Styles/ScrollBars.xaml
    └── Styles/TabsAndSidebar.xaml
```

### Future extension suggestions

- **Multi-cluster support**: add a cluster selector to the sidebar header; each cluster gets its own `ConnectionViewModel` + `MonitorViewModel` pair.
- **Multi-workspace**: allow saving/loading named "workspaces" (connection profile + task set + template directory).
- **Log highlighting**: add regex-based highlight rules per job type in the Logs tab.
- **Job templates gallery**: a library of reusable sbatch templates browsable from the Dashboard.
- **Notifications**: system-tray toasts when a monitored job changes state (RUNNING → COMPLETED / FAILED).

---

## v4 — What's new (Security & Reliability)

| Area | Capability |
|------|-----------|
| **DPAPI credential storage** | Passwords and key passphrases are encrypted with Windows DPAPI before saving; never written in plain text |
| **Save/Load Profile** | Connection settings can be persisted and restored; sensitive fields are transparently encrypted/decrypted |
| **Unified timeouts** | Connection (10 s), command (30 s), and log-fetch (15 s) timeouts — all configurable via `AppSettings` |
| **Cancellation** | Console command execution and log viewer loads both expose a **✕ Cancel** button |
| **Exponential back-off retry** | Network glitches and transient SSH errors are automatically retried up to 3×; auth failures are never retried |
| **Connection state machine** | States: Disconnected / Connecting / Connected / Reconnecting / Error |
| **Auto-reconnect** | When polling detects a dropped connection it enters `Reconnecting`, retries up to 5×, then halts with an error |
| **Reentrancy prevention** | Monitor polling tick skips if a previous refresh is still in flight |
| **Graceful shutdown** | Polling and follow-mode timers are stopped; SSH resources are released when the main window closes |
| **Log UI preservation** | Log viewer errors update only the status bar — previously loaded log lines are never cleared |
| **Error classification** | Auth failures, network errors, timeouts, and missing files produce distinct actionable messages |
| **Rolling local logs** | All key events written to `%AppData%\SlurmJobManager\logs\sjm-YYYYMMDD.log` (7-day rolling) |

---

## v3 — Previously

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

## Security

### Credential storage (DPAPI)

Passwords and SSH private key passphrases are **never written to disk in plain text**.

When you click **💾 Save Profile**, the application:
1. Serialises non-sensitive fields (host, port, username, key path) as JSON to  
   `%AppData%\SlurmJobManager\profile.json`
2. Encrypts the password and passphrase with **Windows Data Protection API (DPAPI)**  
   using `CurrentUser` scope and application-specific entropy
3. Stores only the Base-64 cipher text in the JSON file

On load (**📂 Load Profile**):
- Cipher text is decrypted transparently
- If decryption fails (e.g. profile moved to another machine or user), a warning is shown and the field is cleared — no crash

#### Limitations

- DPAPI is **user- and machine-scoped**: a profile saved on one machine cannot be decrypted on another.  
  Re-enter credentials after migrating to a new PC.
- The password field is held in memory as a plain string during the session (standard WPF constraint with `PasswordBox`).

---

## Stability mechanisms

### Timeouts

| Operation | Default | Setting key |
|-----------|---------|-------------|
| SSH connection | 10 s | `AppSettings.ConnectionTimeout` |
| SSH command | 30 s | `AppSettings.CommandTimeout` |
| Log chunk fetch | 15 s | `AppSettings.LogFetchTimeout` |

### Retry with exponential back-off

Transient failures (network glitches, SSH transport errors, socket errors) are automatically retried:

| Parameter | Default |
|-----------|---------|
| Max retries | 3 |
| Base delay | 1 s (then 2 s, 4 s) |

Authentication failures (`SshAuthenticationException`) are **never** retried — they surface immediately with an actionable message.

Each retry attempt is recorded in the local log file.

### Connection state machine

```
Disconnected
    │  (Connect clicked or auto-reconnect starts)
    ▼
Connecting ──► Error (auth / unreachable)
    │
    ▼
Connected
    │  (SSH drops during polling)
    ▼
Reconnecting ──► Error (threshold exceeded)
    │  (success)
    ▼
Connected (polling resumes)
```

The current state is always visible in the Connection tab status bar.

### Auto-reconnect

When the monitor's polling tick detects a lost connection:
1. Status changes to **↺ Reconnecting…**
2. A reconnect is attempted using the current profile fields
3. If successful, polling resumes transparently
4. After `MaxReconnectAttempts` (default 5) consecutive failures, polling stops and status shows **✗ Error**

### Cancellation

| Operation | How to cancel |
|-----------|---------------|
| SSH command (Console) | Click **✕ Cancel** next to the Run button |
| Log chunk load | Click **✕ Cancel** in the loading indicator bar |

### Graceful shutdown

When the main window closes:
- The monitor polling timer is stopped
- Follow-mode timer is stopped
- Any in-flight load CancellationToken is cancelled
- SSH connections are closed cleanly

---

## Application log file

The application writes structured logs to a daily rolling file:

```
%AppData%\SlurmJobManager\logs\sjm-YYYYMMDD.log
```

Up to **7 days** of log files are retained automatically (older files are deleted).

### What is logged

| Category | Examples |
|----------|---------|
| Connection events | Connect, disconnect, reconnect attempts |
| sbatch submission | Script path, returned job ID, errors |
| Polling errors | Failure count, retry details |
| Log fetch errors | File not found, timeout, SSH errors |
| Retry events | Attempt number, delay, exception message |
| Graceful shutdown | Stop events on application exit |

---

## Common errors and troubleshooting

| Error message | Likely cause | Action |
|---------------|-------------|--------|
| Authentication failed — check username/password or key | Wrong credentials or key type | Verify username, password, or select the correct key file |
| Network unreachable — check host/port and firewall | Wrong host/port, firewall, VPN not active | Ping the host; check VPN; verify port 22 is open |
| Connection timed out | Server slow, host unreachable | Increase `AppSettings.ConnectionTimeout`; check network |
| Failed to decrypt credential | Profile from different user/machine | Re-enter credentials and save the profile again |
| Remote file not found | Wrong path in log viewer | Verify the remote `.out`/`.err` path in the log viewer |
| Polling stopped after N failures | Extended network outage | Restore network, then click **▶ Poll** to restart |

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

## Architecture

```
src/
├── SlurmJobManager.Core/
│   ├── Interfaces/
│   │   ├── IAppLogger.cs             # NEW: application logging abstraction
│   │   ├── IConnectionProfileStore.cs # NEW: encrypted profile persistence
│   │   ├── ICredentialProtector.cs   # NEW: DPAPI encryption abstraction
│   │   ├── ILogChunkService.cs
│   │   ├── ISlurmService.cs
│   │   ├── ISshClientService.cs
│   │   └── ITaskStorageService.cs
│   ├── Models/
│   │   ├── AppSettings.cs            # NEW: timeout/retry/reconnect settings
│   │   └── ...
│   └── Services/
├── SlurmJobManager.Infrastructure/
│   ├── Logs/
│   │   ├── SerilogAppLogger.cs       # NEW: rolling file logger via Serilog
│   │   └── SshLogChunkService.cs     # UPDATED: per-fetch timeout, retry
│   ├── Resilience/
│   │   └── RetryHelper.cs            # NEW: exponential back-off retry
│   ├── Security/
│   │   ├── ConnectionProfileStore.cs # NEW: JSON profile with DPAPI encryption
│   │   └── DpapiCredentialProtector.cs # NEW: DPAPI protect/unprotect
│   ├── Ssh/
│   │   ├── SlurmService.cs           # UPDATED: logger + retry
│   │   └── SshClientService.cs       # UPDATED: configurable timeouts
│   └── Storage/
├── SlurmJobManager.App/
│   ├── Themes/
│   ├── Styles/
│   ├── Converters/
│   ├── ViewModels/
│   │   ├── ConnectionViewModel.cs    # UPDATED: Reconnecting state, save/load profile, error classification
│   │   ├── ConsoleViewModel.cs       # UPDATED: cancel command, logger
│   │   ├── LogViewerViewModel.cs     # UPDATED: cancel command, error preservation, logger
│   │   ├── MonitorViewModel.cs       # UPDATED: reconnect, reentrancy guard, logger, IDisposable
│   │   └── ...
│   ├── Views/
│   │   ├── ConnectionView.xaml       # UPDATED: Save/Load Profile buttons
│   │   ├── ConsoleView.xaml          # UPDATED: Cancel button
│   │   ├── LogViewerView.xaml        # UPDATED: Cancel button in loading indicator
│   │   └── ...
│   └── App.xaml.cs                   # UPDATED: wires logger, DPAPI, settings, graceful shutdown
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
2. **Save profile** — click **💾 Save Profile** to persist credentials (encrypted with DPAPI)
3. **Create task** — set Root Directory + click **New** for a Task ID → **Save Task**
4. **Edit parameter file** — point to a template directory, pick a file, edit, **Save Param File**
5. **Submit** — set Remote Work Directory + App Path → **Submit sbatch Job** → Job ID appears
6. **Monitor** — type your cluster username → **▶ Poll** to start watching `squeue` output
7. **View logs** — paste the remote `.out` / `.err` path → **⟳ Latest** → enable **Follow mode**

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
