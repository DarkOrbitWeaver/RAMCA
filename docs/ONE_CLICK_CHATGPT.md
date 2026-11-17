# ChatGPT connection — one-time setup, then one key

RAMCA can run **OpenAI Secure MCP Tunnel** as an application-owned child process. Once set up,
RAMCA starts it automatically on future launches and kills it when RAMCA exits. No separate
PowerShell window, firewall rule, Cloudflare account, public URL, or always-on service.

## One-time setup (each person does this separately)

1. On [OpenAI Platform > Tunnels](https://platform.openai.com/settings/organization/tunnels),
   create or select **your own** tunnel. Create a **runtime API key** with Tunnels Read + Use.
   Do not use an admin key for everyday connection.
2. Keep the official `tunnel-client.exe` under `RAMCA/tools/tunnel-client/`. When running the
   published app in a checkout's `portable/` folder, RAMCA also recognizes the parent checkout's
   `tools/tunnel-client/` folder.
3. Run RAMCA and press **C**. Paste your tunnel ID and runtime key once; the key entry is hidden.
   Or run `LocalIdeBridge.exe connect-chatgpt` in a terminal; the prompts are identical.
4. RAMCA launches and supervises the tunnel automatically. In ChatGPT choose **Add custom MCP**,
   **Tunnel**, select the existing tunnel, choose **No authentication**, and create the connection.
   The tunnel client supplies RAMCA's local static key through a *local* header, so RAMCA itself
   never accepts anonymous calls. ChatGPT cannot use a plain localhost URL.

After setup: **start RAMCA**, and the tunnel process starts with it. **Quit RAMCA**, and the
owned tunnel process stops. Press **C** to retry after a tunnel-client disconnection.

RAMCA stores its tunnel ID and a runtime key only under its own `data/` directory; on Windows,
key file access is restricted to your account. **Never publish or zip a populated `data/` folder.**
Running `LocalIdeBridge.exe connect-chatgpt --disable` removes this local configuration.

### What this does not do

- It does **not** create a new OpenAI tunnel or runtime API key without an OpenAI account.
- It does **not** install the tunnel client automatically or set up your friend's installation;
  each person gets their own keys and tunnel configuration.
- Starting a child process is **not** proof the ChatGPT connection is healthy. Test MCP discovery
  and a read-only call first.
- ChatGPT write-tool access and availability are governed by account/workspace permissions.
- This feature does not expose RAMCA publicly and does not provide generic connectivity to all
  AI webchats. Those providers must independently support standard MCP and authentication.

### Why not Cloudflare Quick Tunnel?

Cloudflare Quick Tunnels cost nothing and need no account, but their hostnames change on restart,
they have no uptime guarantee, and they do **not** support SSE. They are useful for temporary
HTTP testing, not a durable no-setup RAMCA endpoint. A stable Cloudflare named tunnel requires
one-time Cloudflare/domain configuration; ChatGPT still needs compatible authentication.

The OpenAI private tunnel is the simpler, safer default **for ChatGPT**. For another client that
supports local MCP, RAMCA's `mcp-stdio` does not require any tunnel at all.
