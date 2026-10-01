# MCP runtime acceptance

`McpRuntimeAcceptanceTests` exercises a host-authored plan through `AgenticaRunner`,
the compiled tool catalog, the MCP adapter, the MCP SDK HTTP client, an SDK server,
normalized receipts and observations, and a host completion predicate. The host
installs a read-only `lookup` binding with a pinned input schema and no separate
per-invocation grant requirement.

The fixture holds immutable records and listens on an owned ephemeral loopback
port. Its HTTP shell checks a fixture bearer token and forwards JSON-RPC messages
to `ModelContextProtocol.Server.McpServer` through `StreamableHttpServerTransport`.
The SDK implements discovery, tool dispatch and SSE responses. The server runs
inside the test process; the Lab inspection checks run in actual child processes.
No Docker instance, provider credential, remote server or package download is
required after the locked dependency graph has been restored.

The acceptance verifies:

- Only the installed `lookup` binding reaches the planner; the server's additional
  unbound tool is excluded and never called.
- One remote invocation reads the requested record, with no per-call grant
  consumption for the standing host binding.
- The returned receipt retains the server, tool, pinned schema and content digest.
  Its normalized observation refers to that receipt and the same step.
- Completion requires the expected record ID, title and version in receipt-linked
  structured data. The success outcome cites both pieces of evidence.
- A successful remote response with a missing record produces a failed objective,
  even though its text says the lookup completed successfully. Protocol success
  alone does not satisfy the host's objective.
- `mcp-inspect` uses `AGENTICA_MCP_BEARER_TOKEN` for protected discovery, without
  printing the token or invoking a tool.
- `run --planner deterministic` with either MCP configuration form reports an
  actionable usage error before connecting. That planner is specific to the demo
  catalog. For installed MCP tools, select a supported provider planner or supply
  a host-authored `IWorkflowPlanner` to `AgenticaRunner`.

Run the focused checks from the repository with its pinned SDK:

```powershell
dotnet test Agentica.Tests/Agentica.Tests.csproj --filter FullyQualifiedName~Mcp
```

This proves the bounded read-only path against an actual SDK server. It does not
claim acceptance of third-party server behavior, OAuth negotiation, nontext
content blocks, or mutation-specific objectives. Generic Lab `run` still uses its
documented plan-exhaustion completion policy; application hosts must supply the
predicate that proves their own objective, as this fixture does.
