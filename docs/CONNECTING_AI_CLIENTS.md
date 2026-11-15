# Connecting AI Clients

## Critical Compatibility Rule

**Pasting connection instructions into a normal AI chat box does not automatically give that website access to your computer.**

An external AI client can use LocalIdeBridge **only** if it supports at least one of these three mechanisms:

1. **Standard MCP** — Streamable HTTP (`/mcp`) with custom server URLs, or local `stdio` process launch.
2. **Authenticated HTTP/HTTPS requests** — ability to call `/api/v1/*` (or import `/openapi.json`) with `Authorization: Bearer <KEY>` or `X-LocalIdeBridge-Key: <KEY>`.
3. **Shell execution with a network route** — ability to run `LocalIdeBridge cli ...` or `curl`/`Invoke-RestMethod` against the host.

### Interfaces That Cannot Connect Directly

- Standard consumer webchat sessions offering only built-in web search/file upload with no custom MCP connector or outbound authenticated HTTP action support (e.g., anonymous/free chat tabs, pure benchmark chat arenas without custom MCP/tool support).
- Cloud-hosted AI connectors trying to reach `http://127.0.0.1:43170` (localhost is reachable only from software running on your own PC; cloud connectors require a reachable HTTPS endpoint via `direct_https` or `tunnel`).

## Verification Level Summary

| Interface / Client | Transport | Verification Status | Notes |
| --- | --- | --- | --- |
| Official `@modelcontextprotocol/sdk` client | MCP Streamable HTTP (`POST`/`DELETE` `/mcp`) | **Directly tested end-to-end** (`tests/mcp-client-verify/verify-mcp.mjs`) | Verified full 10-step disposable C# workflow, 52 tools, resources, prompts, session teardown. |
| Custom JSON-RPC MCP clients (2025-03-26 .. 2025-11-25 & stateless 2026-07-28) | MCP Streamable HTTP (JSON + SSE) & `stdio` | **Directly tested in automated suite** | Verified `Mcp-Session-Id`, SSE response frames, `202` notifications, `404` after `DELETE`, stateless mode, and `mcp-stdio`. |
| HTTP/HTTPS JSON clients (`HttpClient`, `curl`, `Invoke-RestMethod`) | HTTP & TLS HTTPS `/api/v1/*` + `/openapi.json` | **Directly tested in automated suite** | Verified Bearer & `X-LocalIdeBridge-Key`, OpenAPI 3.1 schema (61 paths), async jobs, log paging. |
| `LocalIdeBridge cli` | HTTP/HTTPS CLI | **Directly tested in automated suite** | Verified discovery, tool calls, workflows, and exit codes (`0`, `2`, `3`, `4`, `5`, `6`). |
| OpenAI Codex CLI / IDE / Desktop | MCP Streamable HTTP or `stdio` | **Documented config contract** (not live-tested with vendor login in sandbox) | Supports `[mcp_servers.<name>]` with `url` + `bearer_token_env_var`, or local `command` stdio. |
| Claude Code | MCP Streamable HTTP or `stdio` | **Documented config contract** (not live-tested with vendor login in sandbox) | Supports `claude mcp add --transport http` with custom `Authorization: Bearer` header, plus local stdio. |
| Claude Desktop | Local MCP `stdio` (and remote connectors) | **Documented config contract** | Local `claude_desktop_config.json` spawns `LocalIdeBridge.exe mcp-stdio` with zero internet exposure. |
| Gemini CLI | MCP Streamable HTTP or `stdio` | **Documented config contract** | Supports `mcpServers` with `"type": "http"`, `url`, and `Authorization` header, or local `command` stdio. |
| ChatGPT Web / Claude.ai Remote Connectors | Remote MCP over public HTTPS | **Documented limitation — read below** | ChatGPT web Developer Mode MCP connectors require public HTTPS + OAuth (or unauthenticated) rather than static custom Bearer headers; ChatGPT Custom GPT Actions *do* support OpenAPI + Bearer API Key against `/openapi.json`. Claude.ai remote connectors require a publicly reachable HTTPS endpoint and primarily use OAuth. |

## Setup Examples

Generate live templates pre-filled with your current endpoint at any time:

```powershell
.\LocalIdeBridge.exe instructions --client codex
.\LocalIdeBridge.exe instructions --client claude-code
.\LocalIdeBridge.exe instructions --client claude-desktop
.\LocalIdeBridge.exe instructions --client gemini-cli
.\LocalIdeBridge.exe instructions --client http
.\LocalIdeBridge.exe instructions --client cli
```

### 1. OpenAI Codex CLI (`~/.codex/config.toml`)

Set `$env:LOCALIDEBRIDGE_KEY` (via `K` Copy Key in LocalIdeBridge), then add:

```toml
[mcp_servers.localidebridge]
url = "http://127.0.0.1:43170/mcp"
bearer_token_env_var = "LOCALIDEBRIDGE_KEY"
```

Or run over local `stdio` without a running HTTP listener:

```toml
[mcp_servers.localidebridge]
command = "C:\\Tools\\LocalIdeBridge\\LocalIdeBridge.exe"
args = ["mcp-stdio", "--home", "C:\\Tools\\LocalIdeBridge"]
```

### 2. Claude Code

```powershell
$env:LOCALIDEBRIDGE_KEY = "<paste-key>"
claude mcp add --transport http localidebridge "http://127.0.0.1:43170/mcp" --header "Authorization: Bearer $env:LOCALIDEBRIDGE_KEY"
```

For remote access, replace `http://127.0.0.1:43170/mcp` with your `https://<host>/mcp` endpoint.

### 3. Claude Desktop (Windows Local Stdio)

Edit `%APPDATA%\Claude\claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "localidebridge": {
      "command": "C:\\Tools\\LocalIdeBridge\\LocalIdeBridge.exe",
      "args": ["mcp-stdio", "--home", "C:\\Tools\\LocalIdeBridge"]
    }
  }
}
```

### 4. Gemini CLI (`~/.gemini/settings.json`)

```json
{
  "mcpServers": {
    "localidebridge": {
      "type": "http",
      "url": "http://127.0.0.1:43170/mcp",
      "headers": {
        "Authorization": "Bearer $LOCALIDEBRIDGE_KEY"
      }
    }
  }
}
```

### 5. ChatGPT Options and Honest Caveats

- **ChatGPT Desktop + Codex / Codex CLI:** Compatible via Streamable HTTP + `bearer_token_env_var` or local `stdio` as shown above.
- **ChatGPT Custom GPT Actions (HTTPS API):** Compatible when LocalIdeBridge is exposed over valid public HTTPS (`direct_https` with a CA-trusted cert or `tunnel`). Fetch `/openapi.json`, import it as the Action schema, choose **Authentication -> API Key -> Auth Type: Bearer**, and paste your LocalIdeBridge key.
- **ChatGPT Web Developer Mode Custom MCP:** Per current OpenAI documentation, ChatGPT web MCP connectors expect OAuth 2.1 (CIMD/DCR) or no-auth and do not accept a static Bearer token header directly. LocalIdeBridge intentionally refuses unauthenticated remote execution and does not bundle a multi-tenant OAuth authorization server. Use Codex, an MCP bridge, or OpenAPI Bearer Actions instead.

### 6. Direct HTTP/HTTPS API (Any Programmable Agent)

```powershell
$h = @{ Authorization = "Bearer $env:LOCALIDEBRIDGE_KEY" }
Invoke-RestMethod -Uri "http://127.0.0.1:43170/api/v1/tools" -Headers $h
Invoke-RestMethod -Uri "http://127.0.0.1:43170/api/v1/tools/workspace_status/invoke" -Method Post -Headers $h -ContentType "application/json" -Body "{}"
```

### 7. Shell-Capable Agents (`LocalIdeBridge cli`)

```powershell
$env:LOCALIDEBRIDGE_KEY = "<paste-key>"
.\LocalIdeBridge.exe cli configure --endpoint "https://your-host.example" --key-env LOCALIDEBRIDGE_KEY
.\LocalIdeBridge.exe cli tools --json
.\LocalIdeBridge.exe cli call dotnet_test --async --json
.\LocalIdeBridge.exe cli job <jobId> --events --json
```
