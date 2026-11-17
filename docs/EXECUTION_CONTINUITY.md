# Durable execute-once operations (preview, not yet validated)

Remote tool calls can finish locally while the AI client loses the HTTP/MCP response. **Never infer that a timeout means nothing happened.**

RAMCA now exposes two additional tools (subject to clean Windows build/tests):

- `execute_once`: pass `operationId` (a stable, unique value), `tool` (ordinary engineering tool name) and `arguments` (its JSON parameters). RAMCA persists a `running` record **before** invoking the tool. Retrying with the same operation ID never invokes it again.
- `operation_status`: read the persisted `running`, `succeeded`, `failed`, or `interrupted_unknown` state and recorded response.

Suggested flow for a non-idempotent operation:

1. Generate a unique `operationId` such as `vesper-fix123-patch-a38c5f`, and include it in your own task notes.
2. Call `execute_once` with a target tool and its exact arguments.
3. If disconnected, reconnect and query `operation_status` first.
4. You may call `execute_once` again **with the same operation ID and EXACT arguments** to retrieve the prior result or a clear status; no side effects will repeat.
5. If RAMCA crashed mid-execution it changes lingering `running` to `interrupted_unknown`. This is intentionally **not auto-replayed**. Inspect the workspace, changed files and logs, decide whether remediation is needed, and then use a new operation ID only for a deliberately new action.
6. An operation ID reused with different arguments, target tool, or workspace is rejected.

**Limitations:** This requires clients to use `execute_once`; ordinary raw tool invocations without operation IDs are not deduplicated. An `interrupted_unknown` state cannot prove whether execution completed. Crash consistency of file operations is handled separately by the edit transaction manifests. This does not preserve an AI model's reasoning state or guarantee continuing a ChatGPT conversation.

**Do not use on valuable repositories until .NET tests, Windows tests, and reconnect fault injection pass.**
