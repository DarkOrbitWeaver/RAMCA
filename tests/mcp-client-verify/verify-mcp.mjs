#!/usr/bin/env node
/**
 * Independent MCP Streamable HTTP + REST API + CLI end-to-end verification against LocalIdeBridge.
 * Uses the official third-party @modelcontextprotocol/sdk StreamableHTTPClientTransport
 * to execute the full 10-step disposable .NET 10 C# project workflow.
 */
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { spawn, execFileSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

const dotnet = process.env.DOTNET_BIN || "dotnet";
const bridgeDll = path.resolve(process.argv[2] || "src/LocalIdeBridge/bin/Release/net10.0/LocalIdeBridge.dll");

const tmpBase = fs.mkdtempSync(path.join(os.tmpdir(), "lib-indep-mcp-"));
const homeDir = path.join(tmpBase, "LocalIdeBridge");
const wsDir = path.join(tmpBase, "DisposableApp");
fs.mkdirSync(homeDir, { recursive: true });
fs.mkdirSync(wsDir, { recursive: true });

// Seed a disposable C# library + self-verifying test harness with an intentional bug.
fs.writeFileSync(
  path.join(wsDir, "StringUtil.csproj"),
  `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
`
);
fs.writeFileSync(
  path.join(wsDir, "Slugifier.cs"),
  `namespace StringUtil;

public static class Slugifier
{
    public static string ToSlug(string input)
    {
        // Bug: uses underscores instead of hyphens
        return input.Trim().ToLowerInvariant().Replace(' ', '_');
    }
}
`
);
fs.writeFileSync(
  path.join(wsDir, "Program.cs"),
  `using StringUtil;
var slug = Slugifier.ToSlug("Hello Local Ide Bridge");
if (slug != "hello-local-ide-bridge")
{
    Console.Error.WriteLine($"Expected 'hello-local-ide-bridge' but got '{slug}'");
    return 1;
}
Console.WriteLine($"VERIFIED_OK: {slug}");
return 0;
`
);

let hostProc = null;

function log(step, detail) {
  console.log(`[PASS] ${step}: ${detail}`);
}

try {
  // Run scripted first-run setup
  execFileSync(dotnet, [bridgeDll, "setup", "--home", homeDir, "--workspace", wsDir, "--alias", "demo", "--mode", "localhost", "--port", "0"], {
    stdio: "pipe",
  });
  const key = fs.readFileSync(path.join(homeDir, "data", "secret.key"), "utf8").trim();

  // Launch headless LocalIdeBridge host subprocess
  const t0 = Date.now();
  hostProc = spawn(dotnet, [bridgeDll, "host", "--home", homeDir], { stdio: ["ignore", "pipe", "pipe"] });

  const pidFile = path.join(homeDir, "data", "host.pid");
  let baseUrl = null;
  for (let i = 0; i < 100; i++) {
    if (fs.existsSync(pidFile)) {
      const parts = fs.readFileSync(pidFile, "utf8").trim().split("|");
      if (parts[1]) {
        try {
          const r = await fetch(`${parts[1]}/health`);
          if (r.ok) {
            baseUrl = parts[1];
            break;
          }
        } catch {}
      }
    }
    await new Promise((r) => setTimeout(r, 25));
  }
  if (!baseUrl) throw new Error("Timed out waiting for LocalIdeBridge host to come online.");
  log("0. Subprocess Cold Start", `Host online at ${baseUrl} in ${Date.now() - t0}ms`);

  // Step 1: Connect with official @modelcontextprotocol/sdk StreamableHTTPClientTransport
  const transport = new StreamableHTTPClientTransport(new URL(`${baseUrl}/mcp`), {
    requestInit: {
      headers: { Authorization: `Bearer ${key}` },
    },
  });
  const client = new Client({ name: "official-mcp-ts-sdk-verifier", version: "1.0.0" });
  await client.connect(transport);
  log("1. MCP Connect", `Connected via official @modelcontextprotocol/sdk (sessionId=${transport.sessionId})`);

  // Step 2: Discover tools, resources, and prompts
  const { tools } = await client.listTools();
  const { resources } = await client.listResources();
  const { prompts } = await client.listPrompts();
  if (tools.length < 50) throw new Error(`Expected >=50 tools, found ${tools.length}`);
  log("2. MCP Discover", `${tools.length} tools, ${resources.length} resources, ${prompts.length} prompts`);

  // Step 3: Select workspace
  const sel = await client.callTool({ name: "workspace_select", arguments: { workspace: "demo" } });
  if (sel.isError) throw new Error("workspace_select failed");
  log("3. Select Workspace", sel.structuredContent.summary);

  // Initialize Git repository and baseline commit
  await client.callTool({ name: "git_branch", arguments: { action: "init", name: "main" } });
  await client.callTool({ name: "git_commit", arguments: { message: "chore: initial project", stageAll: true } });

  // Step 4: Read source file
  const readRes = await client.callTool({ name: "read_file", arguments: { path: "Slugifier.cs" } });
  if (readRes.isError) throw new Error("read_file failed");
  const beforeSha = readRes.structuredContent.data.sha256;
  log("4. Read Source", `Slugifier.cs sha256=${beforeSha.slice(0, 12)}...`);

  // Step 5: Semantic code search via Roslyn
  const searchRes = await client.callTool({ name: "find_references", arguments: { symbolName: "ToSlug" } });
  if (searchRes.isError) throw new Error("find_references failed");
  log(
    "5. Semantic Search",
    `engine=${searchRes.structuredContent.data.engine}, refs=${searchRes.structuredContent.data.count}`
  );

  // Step 6: Apply unified diff patch
  const patch = [
    "--- a/Slugifier.cs",
    "+++ b/Slugifier.cs",
    "@@ -4,7 +4,6 @@",
    " {",
    "     public static string ToSlug(string input)",
    "     {",
    "-        // Bug: uses underscores instead of hyphens",
    "-        return input.Trim().ToLowerInvariant().Replace(' ', '_');",
    "+        return input.Trim().ToLowerInvariant().Replace(' ', '-');",
    "     }",
    " }",
  ].join("\n");
  const patchRes = await client.callTool({
    name: "apply_patch",
    arguments: { patch, expectedSha256: beforeSha },
  });
  if (patchRes.isError) throw new Error(`apply_patch failed: ${JSON.stringify(patchRes)}`);
  log("6. Apply Patch", patchRes.structuredContent.summary);

  // Step 7: Compile with dotnet_build
  const buildRes = await client.callTool({ name: "dotnet_build", arguments: { target: "StringUtil.csproj" } });
  if (buildRes.isError) throw new Error(`dotnet_build failed: ${JSON.stringify(buildRes)}`);
  log("7. Compile", buildRes.structuredContent.summary);

  // Step 8: Run verification harness
  const testRes = await client.callTool({
    name: "run_command",
    arguments: { command: "dotnet", arguments: ["run", "--no-build", "--project", "StringUtil.csproj"] },
  });
  if (testRes.isError || !JSON.stringify(testRes).includes("VERIFIED_OK: hello-local-ide-bridge")) {
    throw new Error(`Test execution failed: ${JSON.stringify(testRes)}`);
  }
  log("8. Run Verification", "Program exited 0 with 'VERIFIED_OK: hello-local-ide-bridge'");

  // Step 9: Create local Git commit
  const commitRes = await client.callTool({
    name: "git_commit",
    arguments: { message: "fix: replace spaces with hyphens in Slugifier.ToSlug", stageAll: true },
  });
  if (commitRes.isError) throw new Error("git_commit failed");
  const commitSha = commitRes.structuredContent.data.shortSha;
  log("9. Local Git Commit", `Committed ${commitSha} on main`);

  // Step 10: Read final status via MCP, secondary HTTP API, AND CLI client
  const statusMcp = await client.callTool({ name: "workspace_status", arguments: {} });
  const statusHttp = await fetch(`${baseUrl}/api/v1/info`, {
    headers: { Authorization: `Bearer ${key}` },
  }).then((r) => r.json());
  const openApi = await fetch(`${baseUrl}/openapi.json`, {
    headers: { Authorization: `Bearer ${key}` },
  }).then((r) => r.json());
  const cliOut = JSON.parse(
    execFileSync(dotnet, [bridgeDll, "cli", "call", "git_status", "--home", homeDir, "--json"], {
      encoding: "utf8",
    })
  );

  if (statusMcp.structuredContent.data.modifiedOrUntrackedCount !== 0) {
    throw new Error("Expected clean git tree after commit");
  }
  if (!cliOut.success) throw new Error("CLI git_status failed");
  log(
    "10. Final Status (MCP + HTTP + OpenAPI + CLI)",
    `clean tree, branch=${statusMcp.structuredContent.data.gitBranch}, totalCalls=${statusHttp.totalInvocations}, openApiPaths=${Object.keys(openApi.paths).length}`
  );

  // Clean MCP session termination & host shutdown check
  await transport.terminateSession();
  await client.close();

  hostProc.kill("SIGINT");
  await new Promise((resolve) => hostProc.once("exit", resolve));
  hostProc = null;

  let unreachable = false;
  try {
    await fetch(`${baseUrl}/health`);
  } catch {
    unreachable = true;
  }
  if (!unreachable) throw new Error("Listener remained reachable after host shutdown!");
  log("11. Listener Shutdown", "Server terminated cleanly and released port immediately");
  console.log("\nALL INDEPENDENT COMPATIBILITY CHECKS PASSED.");
} finally {
  if (hostProc) {
    try { hostProc.kill("SIGKILL"); } catch {}
  }
  fs.rmSync(tmpBase, { recursive: true, force: true });
}
