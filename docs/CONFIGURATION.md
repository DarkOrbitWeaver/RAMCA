# Configuration Reference

LocalIdeBridge uses one readable, versioned file at `<portable-root>/config.json` (`schemaVersion: 1`).
Secrets are **never** stored in `config.json`; the generated access key lives strictly in `data/secret.key`.

See [`config.example.json`](../config.example.json) for a complete template.

## CLI Configuration Commands

```powershell
.\LocalIdeBridge.exe setup                                      # Interactive setup wizard
.\LocalIdeBridge.exe setup --workspace C:\Work\App --mode localhost --port 43170
.\LocalIdeBridge.exe workspace add C:\Work\Other --alias other  # Authorize another folder
.\LocalIdeBridge.exe workspace select other                     # Change default workspace
.\LocalIdeBridge.exe config show                                # Print effective config.json
.\LocalIdeBridge.exe config export > shareable.json             # Export without local cert/endpoint fields
.\LocalIdeBridge.exe config reset --rotate-key                  # Reset settings and regenerate key
.\LocalIdeBridge.exe key rotate                                 # Rotate secret key immediately
```

## Top-Level Settings

| Key | Type | Default | Description |
| --- | --- | --- | --- |
| `schemaVersion` | `int` | `1` | Configuration contract version. |
| `setupCompleted` | `bool` | `false` | When `false`, interactive launch runs the first-time setup wizard. |
| `trustedWorkspaces` | `array` | `[]` | Authorized folders `{ name, path, addedAtUtc }`. |
| `defaultWorkspace` | `string?` | `null` | Workspace automatically activated at startup. |

## `connection`

| Key | Type | Default | Description |
| --- | --- | --- | --- |
| `mode` | `string` | `"localhost"` | `"localhost"`, `"direct_https"`, or `"tunnel"`. |
| `listenHost` | `string` | `"127.0.0.1"` | Bind address (`"127.0.0.1"`, `"::1"`, `"0.0.0.0"`, or specific interface IP). |
| `port` | `int` | `43170` | TCP listening port (`0` selects an OS-assigned ephemeral port). |
| `useHttps` | `bool` | `false` | Enables Kestrel TLS (required on non-loopback addresses when `requireHttpsForRemote=true`). |
| `tlsCertPath` | `string?` | `null` | Path to a PKCS#12 (`.pfx`) TLS certificate for `direct_https`. |
| `tlsCertPasswordEnvVar` | `string?` | `null` | Name of an environment variable holding the `.pfx` password (keeps passwords out of `config.json`). |
| `autoGenerateSelfSignedTls` | `bool` | `false` | Generates/reuses `data/selfsigned-tls.pfx` (ECDSA P-256) for LAN/direct clients that trust or pin it. |
| `publicEndpoint` | `string?` | `null` | Externally visible base URL advertised in instructions and OpenAPI (e.g. `"https://dev.example.com:43170"`). |
| `tunnelProvider` | `string` | `"none"` | `"none"`, `"cloudflared"`, `"ssh"`, or `"custom"`. Process is owned by LocalIdeBridge and killed on exit. |
| `tunnelExecutable` | `string?` | `null` | Optional path/name override (searches `tools/` first, then `PATH`). |
| `tunnelArguments` | `string[]` | `[]` | Custom CLI arguments passed to the tunnel process. |

## `autonomy`

| Key | Type | Default | Description |
| --- | --- | --- | --- |
| `allowFileWrites` | `bool` | `true` | Permit creating, modifying, patching, and renaming files in trusted workspaces. |
| `allowFileDeletes` | `bool` | `true` | Permit deleting ordinary workspace files (backups kept in `data/sessions`). |
| `allowCommands` | `bool` | `true` | Permit supervised developer command execution (`dotnet`, `git`, `msbuild`, etc.). |
| `allowShellScripts` | `bool` | `true` | Permit `run_command` with `useShell=true` (PowerShell). |
| `allowDependencyInstall` | `bool` | `true` | Permit project dependency restore/install tools. |
| `allowGitCommits` | `bool` | `true` | Permit unattended local Git commits. |
| `allowGitWorktrees` | `bool` | `true` | Permit sandboxed local Git worktree creation/removal. |
| `gitPushPolicy` | `string` | `"deny"` | `"deny"` (default) blocks external pushes; `"allow"` enables `git_push`. |
| `redirectChildTempToPortable` | `bool` | `false` | Redirects child `TEMP`/`TMP` into `LocalIdeBridge/temp` when desired. |
| `allowedExecutables` | `string[]` | `["dotnet","git","msbuild",...]` | Approved tool names runnable via `run_command`. |
| `blockedCommandPatterns` | `string[]` | `[...]` | Denylist of destructive/admin patterns (firewall edits, disk formatting, service/task creation, Defender tampering, shutdown). |

## `security`

| Key | Type | Default | Description |
| --- | --- | --- | --- |
| `requestsPerMinute` | `int` | `600` | Sliding-window max requests per minute per client IP (`10..100000`). |
| `failedAuthPerMinute` | `int` | `20` | Failed authentication attempts per minute before temporary `429` lockout (`3..200`). |
| `maxRequestBodyBytes` | `long` | `4194304` (4 MB) | Maximum accepted HTTP request body size (`413` above limit). |
| `requireHttpsForRemote` | `bool` | `true` | Rejects plaintext HTTP bindings and requests from non-loopback addresses. |
| `allowedOrigins` | `string[]` | `[]` | Additional allowed browser `Origin` headers (protects against DNS rebinding). |

## `retention` & `resources`

| Key | Default | Description |
| --- | --- | --- |
| `retention.maxLogAgeDays` | `14` | Automatic cleanup threshold for `logs/*.log` files (`1..365`). |
| `retention.maxSessionCount` | `100` | Maximum stored sessions before oldest closed sessions and logs are pruned (`5..5000`). |
| `retention.maxSingleLogBytes` | `16777216` (16 MB) | Hard disk size cap per command execution log. |
| `retention.maxToolOutputChars` | `16000` | Maximum inline log/output slice returned per call. |
| `retention.editJournalMaxEntries` | `250` | Maximum reversible edit backup snapshots retained on disk. |
| `resources.maxConcurrentJobs` | `4` | Max simultaneous background jobs (`1..32`). |
| `resources.maxParallelReadSteps` | `4` | Max concurrent read-only steps in `workflow_run` (`1..16`). |
| `resources.defaultCommandTimeoutSeconds` | `180` | Default per-command timeout (`5..7200`). |
| `resources.maxCommandTimeoutSeconds` | `1800` | Upper bound for any requested timeout (`10..28800`). |
| `resources.childProcessMemoryLimitMb` | `0` | Optional Windows Job Object per-process memory cap (`0` = uncapped). |
| `resources.memoryCacheMaxEntries` | `64` | Maximum cached Roslyn syntax trees in LRU cache (`8..1024`). |
| `resources.memoryCacheMaxBytes` | `4194304` (4 MB) | Maximum byte budget for in-memory LRU cache. |
| `resources.tuiEventBufferSize` | `200` | Fixed ring-buffer size for recent activity events (`32..1000`). |

## `tui` & `integrations`

- `tui.theme`: `"minimal"` (default, restrained ANSI accents), `"high_contrast"`, or `"mono"` (also respects `NO_COLOR`).
- `tui.refreshIntervalMs`: Event-coalescing debounce interval (`500` ms default).
- `integrations.enableRoslynSemantic`: Enable/disable Roslyn C# analysis (`true`).
- `integrations.enableOpenApi`: Expose authenticated `/openapi.json` (`true`).
- `integrations.enableMcpSseStream`: Allow `GET /mcp` SSE stream (`true`).

## Optional Per-Repository Policy (`.localidebridge.json`)

Place a `.localidebridge.json` file in the root of a trusted workspace to tighten rules or authorize project-specific tools (LocalIdeBridge prevents connected agents from editing `.localidebridge.json`):

```json
{
  "readOnlyPaths": ["docs/legal", "Directory.Build.props"],
  "deniedPaths": [".env", "secrets"],
  "approvedProjectExecutables": ["my-codegen"],
  "allowGitPush": false,
  "allowFileDeletes": true,
  "allowShellScripts": true
}
```
