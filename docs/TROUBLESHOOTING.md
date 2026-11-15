# Troubleshooting

Run `.\LocalIdeBridge.exe doctor` first to inspect installed tools, port availability, TLS readiness, and NAT/CGNAT interface status.

## Connection & Networking

### Port 43170 already in use
- **Cause:** Another LocalIdeBridge instance or local application is bound to `43170`.
- **Fix:** Close the existing instance, or run `.\LocalIdeBridge.exe setup --port 43175` (or launch once with `.\LocalIdeBridge.exe host --port 43175`).

### Cloud AI client cannot reach `http://127.0.0.1:43170/mcp`
- **Cause:** `127.0.0.1` is loopback-only and reachable only by agents running on your own PC (Codex CLI, Claude Code, Claude Desktop, Gemini CLI, local scripts).
- **Fix:** Use either `direct_https` (if you own a reachable public IP and valid TLS certificate) or `tunnel` mode (`cloudflared` or `ssh -R`).

### Behind residential NAT (`192.168.x.x`, `10.x.x.x`, `172.16-31.x.x`)
- `doctor` reports `Private NAT IPv4: True`.
- Direct inbound internet connections will not reach your PC unless you manually configure router port forwarding to your PC's LAN IP, allow the TCP port in Windows Defender Firewall, and configure a valid TLS certificate. LocalIdeBridge never modifies router or firewall settings on its own.
- Easier alternative: switch to `tunnel` mode so `listenHost` stays `127.0.0.1`.

### Behind Carrier-Grade NAT (`100.64.0.0/10`)
- `doctor` reports `CGNAT (100.64/10): True`.
- Router port forwarding does **not** work over ISP CGNAT (note: private overlay VPNs like Tailscale also assign `100.64/10` addresses—those *do* work directly between devices on the same mesh).
- For public cloud agents behind CGNAT, place `cloudflared.exe` in `LocalIdeBridge\tools\` (or configure `ssh -R` to a small host you control) and run `.\LocalIdeBridge.exe setup --mode tunnel --tunnel cloudflared`.

### TLS certificate rejection
- Self-signed certificates (`autoGenerateSelfSignedTls: true`) encrypt traffic and work with `LocalIdeBridge cli --insecure-tls` or clients configured to trust `data\selfsigned-tls.pfx`, but cloud-hosted AI services reject untrusted CAs.
- Supply a CA-issued `.pfx` via `connection.tlsCertPath` (e.g. Let's Encrypt / Caddy / reverse proxy) or terminate TLS via an owned HTTPS tunnel.

## Authentication & Policy Errors

- **`401 unauthorized`:** Missing or wrong key, or key was rotated with `R` / `key rotate`. Copy the current key (`K` or `.\LocalIdeBridge.exe key copy`) and ensure your header is `Authorization: Bearer lib_...`.
- **`429 auth_rate_limited`:** Exceeded `security.failedAuthPerMinute` (default `20`). Fix the bad credential in your client and wait 60 seconds.
- **`403 forbidden_origin`:** A browser-based client sent an untrusted `Origin` header. Add the exact trusted origin URL to `security.allowedOrigins` in `config.json`.
- **`403 host_paused`:** Execution is paused in the supervisor TUI. Press `P` to resume.
- **`403 authorization_required` on `git_push`:** External push is disabled by default. Commit locally, push yourself, or explicitly set `"gitPushPolicy": "allow"` in `config.json`.

## Tool, Edit, and Build Errors

- **`409 stale_file` / `precondition_failed`:** The file changed since `read_file`, or unified diff context lines did not match. Call `read_file` / `read_range` again to get current lines and the latest `sha256`, then retry.
- **`dotnet` or `msbuild` not found:** Install the .NET 10 SDK (or Visual Studio Build Tools) and ensure `dotnet` is on `PATH`. Confirm with `.\LocalIdeBridge.exe doctor`.
- **In-process Roslyn vs full build diagnostics:** `compiler_diagnostics` with `mode="roslyn"` checks against core BCL assemblies only; use the default `mode="build"` whenever NuGet packages or source generators are involved.

## Shutdown & Crash Recovery

- Closing `LocalIdeBridge.exe` (`Q`, `Ctrl+C`, or closing the terminal window) stops Kestrel immediately and closes the Windows Job Object handle, terminating all owned builds, tests, shells, and tunnels.
- If a prior session was interrupted mid-job (e.g. power loss), the next launch marks unfinished jobs as `interrupted_restart` without re-running non-idempotent commands. Inspect them with `.\LocalIdeBridge.exe cli jobs --status interrupted_restart --json` and review `git_status` before continuing.
