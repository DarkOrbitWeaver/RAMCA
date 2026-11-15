# LocalIdeBridge Carbon 2 — targeted fixes and required validation

**Updated:** November 15, 2026. This is a **source-code ZIP**; no updated Windows executable is included. The original source author's earlier test/benchmark reports are retained as historical references and do not certify these changes.

## Changes made

1. **SQLite advisory:** updated `Microsoft.Data.Sqlite` to `10.0.15` and added a direct reference to `SQLitePCLRaw.bundle_e_sqlite3 2.1.14`. NuGet documents that Sqlite 10.0.15 normally requires SQLitePCLRaw 2.1.13 or newer, while `GHSA-2m69-gcr7-jv3q` applies to `SQLitePCLRaw.lib.e_sqlite3` versions <= 2.1.11. NuGet restore/audit still needs to be performed on the actual build machine.
2. **Reversible deletion:** ordinary `delete_file` operations now retain a byte-for-byte backup up to 256 MiB instead of silently dropping the backup for files above 8 MiB. Files above 256 MiB return `backup_limit_exceeded` unless the caller deliberately supplies `allowIrreversible=true`. This limit is intended to prevent unattended deletion from unexpectedly filling the companion's portable data directory. Deletion is *not* reversible if irreversible mode was explicitly selected.
3. **Restore integrity:** `revert_edit` verifies the backup snapshot's SHA-256 against its recorded original checksum. If the backup was tampered with or corrupted, it refuses to overwrite the current file.
4. **Origin checks:** public-endpoint Origin matching now requires the exact scheme, host and port instead of just the hostname.
5. **New tests:** added one larger-than-8-MiB deletion/restore test, one tampered-backup rejection test, and one public Origin scheme/port mismatch test.

## Windows validation commands

Run these from the project directory on a PC with the **.NET 10 SDK**:

```powershell
dotnet restore .\LocalIdeBridge.sln
dotnet build .\LocalIdeBridge.sln -c Release --no-restore
dotnet test .\LocalIdeBridge.sln -c Release --no-build
dotnet list .\src\LocalIdeBridge\LocalIdeBridge.csproj package --vulnerable --include-transitive
```

Then perform the disposable-workspace end-to-end test described in `docs/GETTING_STARTED.md` before trusting an important project. To build a Windows release on your Windows PC:

```powershell
dotnet publish .\src\LocalIdeBridge\LocalIdeBridge.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\publish
```

## ChatGPT MCP compatibility — **not automatically fixed by these changes**

Carbon 2 exposes MCP Streamable HTTP at `/mcp`, and a simple bearer-token authentication scheme. **Current ChatGPT custom MCP connections generally use OAuth or no authentication; Carbon 2 does not implement OAuth 2.1**. Choosing "No authentication" against Carbon 2 will fail with `401 Unauthorized`; do not disable authentication on a remote code-execution service just to make it connect.

If your account/workspace exposes **Plugins → + → Add custom MCP server**, the connection form can be tested, but plan/workspace permissions still govern whether write tools can be installed and invoked. Official OpenAI guidance currently lists full custom MCP support for ChatGPT Business and Enterprise/Edu; having the setting visible is not proof of Plus write-tool access.

Possible routes:
- An eligible custom MCP connection after a properly implemented OAuth 2.1 authorization flow has been added and tested.
- A separately configured existing Custom GPT *Actions* integration using Carbon 2's OpenAPI schema and API-key bearer authentication, if your account still supports that legacy feature. This is a different interface from a custom MCP plugin, and is subject to its own restrictions.
- Clients that accept custom bearer headers for remote MCP (e.g. compatible desktop/CLI agents) can use the existing interface once reachable and authenticated.

**A public HTTPS URL is still necessary for remote web services.** Localhost-only URLs are not reachable from ChatGPT's servers. Do not publish an unauthenticated endpoint or paste the master key into an untrusted chat.

## Unfinished functionality

- Complete review-before-apply and review-before-commit approval modes are not implemented; ordinary autonomous editing plus checked `revert_edit` remains the default.
- Recovering earlier edits after subsequent overlapping edits requires a real merge or conflict UI, not just restoring a snapshot.
- OAuth 2.1 for browser-based MCP clients remains to be implemented and verified with actual accounts.
- Live Windows crash/process-tree shutdown, networking, CLI integration, and resource performance are not verified by this source-only patch.
