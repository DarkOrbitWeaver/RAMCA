# Security Model

LocalIdeBridge exposes real development commands on a personal workstation. Its security design focuses on simple, verifiable enforcement without enterprise complexity or constant approval popups.

## 1. Authentication & Credential Hygiene

- **Generation:** At first launch, `CredentialStore` generates 32 bytes (256 bits) of entropy using `System.Security.Cryptography.RandomNumberGenerator`, encoded as `lib_<base64url>`, and zeros the stack buffer.
- **Isolated storage:** Saved only in `data/secret.key` with current-user-only ACLs (`icacls /inheritance:r /grant:r <user>:(R,W)` on Windows; `0600` on POSIX). Never written to `config.json`, `config export`, or default connection instructions.
- **Verification:** Incoming keys (`Authorization: Bearer <key>` or `X-LocalIdeBridge-Key: <key>`) are SHA-256 hashed and checked via `CryptographicOperations.FixedTimeEquals` (constant time). URL query-string keys are intentionally rejected so tokens never leak via URL histories or proxy logs.
- **Rotation:** Press `R` in the TUI or run `LocalIdeBridge.exe key rotate`. The old key stops working immediately on the very next HTTP/MCP request.
- **Log scrubbing:** `RedactingLogger` and `LogStore` automatically replace the active key, the previously rotated key, `lib_*` tokens, and `Authorization`/`X-LocalIdeBridge-Key` headers with `[REDACTED_KEY]`.
- **Unauthenticated surface:** Only `GET /health` responds without authentication, returning solely `{"status":"online","service":"LocalIdeBridge","version":"1.0.0"}` (no paths, tools, or session IDs). `/mcp`, `/api/v1/*`, and `/openapi.json` all require authentication.

## 2. Network & Request Protections

- **Default localhost bind:** First-run default binds strictly to `127.0.0.1` (unreachable from LAN or internet).
- **Mandatory HTTPS for direct remote access:** Binding a non-loopback address without TLS is refused at startup when `security.requireHttpsForRemote` is `true`.
- **No silent exposure:** LocalIdeBridge never invokes UPnP/NAT-PMP router mapping and never modifies Windows Defender Firewall rules.
- **Rate & size limits:** Per-IP sliding-window request cap (`600/min` default), strict failed-auth lockout (`20/min` -> `429 Too Many Requests`), and Kestrel + middleware request body cap (`4 MB` default -> `413 Payload Too Large`).
- **DNS-rebinding defense:** Requests carrying a browser `Origin` header are rejected (`403`) unless the full origin authority (scheme, hostname, **and port**) matches the configured endpoint or an explicit `security.allowedOrigins` entry.

## 3. Trusted Workspace Boundary

`WorkspaceGuard` enforces filesystem boundaries in the host rather than trusting AI prompt compliance:

- Refuses to authorize filesystem roots (`C:\`), Windows system/program directories, or LocalIdeBridge's own `data/` directory (protecting `secret.key` and `state.db`).
- Canonicalizes every relative or absolute path and rejects `..` traversal, drive switches, and NUL bytes.
- Inspects **every existing path segment** for NTFS reparse points (directory junctions and symlinks) and resolves final link targets, rejecting reads, writes, file creation, tree listings, and searches that escape the trusted workspace.
- Blocks agent writes to `.git/hooks/*`, `.git/config`, and `.localidebridge.json` to prevent silent hook persistence or self-escalation.
- Blocks external `git_push` by default until explicitly enabled in `config.json`.

## 4. Honest Limitations of Command Execution

Do not mistake workspace path validation for a hardware VM sandbox:

- When `autonomy.allowCommands` / `allowShellScripts` is enabled (the default for productive software engineering), approved tools such as `dotnet build` (which executes MSBuild targets and NuGet build tasks), `dotnet test` (which runs project test code), and `pwsh`/`powershell` run under your Windows user account.
- LocalIdeBridge restricts executable names, blocks common administrative/destructive command patterns, enforces working directories, applies timeouts, and kills spawned process trees via a Windows Job Object—but arbitrary code inside a project build, test suite, or PowerShell command can still access files readable by your Windows user account.
- **Practical guidance:**
  - If you want strictly file-confined editing with zero arbitrary shell execution, set `"allowShellScripts": false` (or `"allowCommands": false`) in `config.json` or `.localidebridge.json`.
  - Keep `LocalIdeBridge.exe` closed when you are not actively using it; closing immediately stops the listener and any owned tunnel.
  - Rotate your key (`R` or `key rotate`) immediately if you ever paste it into an untrusted prompt or log.
