# Getting Started

## 1. Extract the Portable Folder

Unzip `LocalIdeBridge-win-x64-portable.zip` anywhere accessible to your Windows user account, such as `C:\Tools\LocalIdeBridge`. No installer, administrator elevation, or Windows service is required.

Directory layout after first launch:

```text
LocalIdeBridge/
  LocalIdeBridge.exe      Self-contained .NET 10 executable (supervisor, host, MCP, CLI)
  config.json             Versioned, shareable settings (never contains secrets)
  data/
    secret.key            256-bit access key (restricted ACL, excluded from logs/exports)
    state.db              SQLite WAL metadata store (sessions, jobs, edits, workflows)
    sessions/             Per-session streaming command logs and reversible edit backups
  logs/                   Daily rotating host logs with automatic secret redaction
  cache/                  Bounded local caches
  temp/                   Ephemeral files (cleaned at startup and shutdown)
  sandbox/                Disposable scratch projects and Git worktrees
  tools/                  Optional drop-in portable binaries (e.g. cloudflared.exe)
  docs/                   Reference manuals
```

## 2. First-Run Setup

### Option A: Interactive Wizard

Double-click `LocalIdeBridge.exe` or run it from Windows Terminal / PowerShell:

```powershell
cd C:\Tools\LocalIdeBridge
.\LocalIdeBridge.exe
```

1. Enter a trusted project folder (or press `Enter` to create `sandbox\starter-project`).
2. Review detected developer tools (`dotnet`, `git`, `pwsh`/`powershell`, `msbuild`, optional tunnel binaries).
3. Choose connection mode: `[1] Localhost`, `[2] Direct remote HTTPS`, or `[3] Lightweight tunnel`.
4. Confirm listening port (default `43170`).

### Option B: Command-Line Setup

```powershell
.\LocalIdeBridge.exe setup --workspace C:\Projects\DemoCalc --create-workspace --alias demo --mode localhost --port 43170
.\LocalIdeBridge.exe doctor
```

## 3. Supervisor Interface Controls

While running interactively, LocalIdeBridge stays quiet and updates only on events or keypresses:

- `E` — Copy MCP endpoint URL (`http://127.0.0.1:43170/mcp` or configured HTTPS URL)
- `K` — Copy secret access key to clipboard (displayed masked on screen)
- `I` — Copy AI-readable connection instructions (without secret key)
- `R` — Regenerate/rotate access key immediately (invalidates previous key)
- `W` — Cycle between trusted workspaces
- `S` — Start a new session
- `P` — Pause/resume write and execution tools (read-only inspection stays online)
- `N` — Set a session steering note readable via `workspace_status` / `bridge://session/active`
- `1`..`5` — Switch virtualized views (Recent Calls, Edits/Diffs, Jobs/Builds, Git/Sessions, Errors)
- `Q` or `Ctrl+C` — Stop listener, terminate all owned child processes, and exit

For headless operation: `.\LocalIdeBridge.exe host`.

## 4. Complete Disposable-Project Walkthrough

Open a second PowerShell window while `LocalIdeBridge.exe` is running to test a full edit-build-test-commit cycle inside a disposable folder:

```powershell
cd C:\Tools\LocalIdeBridge

# 1. Create and authorize a disposable project in sandbox\
.\LocalIdeBridge.exe workspace add .\sandbox\walkthrough --alias wt --default
.\LocalIdeBridge.exe cli call workspace_select --arg workspace=wt --json

# 2. Seed a tiny .NET 10 project with a deliberate bug via LocalIdeBridge tools
.\LocalIdeBridge.exe cli call run_command --arg command=dotnet --json-args '{"command":"dotnet","arguments":["new","console","-n","CalcApp","-o",".","--force"]}' --json
.\LocalIdeBridge.exe cli call create_file --json-args '{"path":"Program.cs","overwrite":true,"content":"int Add(int a, int b) => a - b;\nif (Add(2, 3) != 5) { Console.Error.WriteLine(\"FAIL\"); return 1; }\nConsole.WriteLine(\"PASS\");\nreturn 0;\n"}' --json

# 3. Initialize Git and make a baseline commit
.\LocalIdeBridge.exe cli call git_branch --arg action=init --arg name=main --json
.\LocalIdeBridge.exe cli call git_commit --json-args '{"message":"chore: seed disposable project","stageAll":true}' --json

# 4. Inspect, patch, build, verify, and commit
.\LocalIdeBridge.exe cli call read_file --arg path=Program.cs --arg includeLineNumbers=true --json
.\LocalIdeBridge.exe cli call replace_range --json-args '{"path":"Program.cs","startLine":1,"endLine":1,"newText":"int Add(int a, int b) => a + b;"}' --json
.\LocalIdeBridge.exe cli call dotnet_build --json
.\LocalIdeBridge.exe cli call run_command --json-args '{"command":"dotnet","arguments":["run","--no-build"]}' --json
.\LocalIdeBridge.exe cli call git_commit --json-args '{"message":"fix: add instead of subtract","stageAll":true}' --json

# 5. Inspect final status and session audit trail
.\LocalIdeBridge.exe cli call git_status --json
.\LocalIdeBridge.exe cli sessions --json
```

When finished, delete `sandbox\walkthrough` without affecting any real projects.

## 5. Sharing with a Friend

Your friend only needs `LocalIdeBridge-win-x64-portable.zip`:

1. Unzip to a folder, launch `LocalIdeBridge.exe`, pick their own project folder.
2. Their instance automatically creates its own unique `data\secret.key`.
3. If you want to share non-secret preferences, run `.\LocalIdeBridge.exe config export > shared-config.json` (secrets and local certificate paths are stripped automatically).
