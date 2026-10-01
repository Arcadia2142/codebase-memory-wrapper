# codebase-memory-wrapper

Minimal local MCP Streamable HTTP wrapper for `codebase-memory-mcp`.

The wrapper exposes one local MCP endpoint and forwards tools, prompts, and backend
instructions to one lazy stdio `codebase-memory-mcp` child process. Requests can arrive
concurrently, but the child receives only one request at a time.

## Compatibility

This version of the wrapper targets and has been tested with
`codebase-memory-mcp 0.11.0`. Compatibility with other backend versions has not
been verified for this revision.

See [the compatibility report](docs/compatibility-0.11.0.md) for verified tools,
parallel requests, child recovery, and known upstream limitations. Backend Cypher
and graph-comparison limitations also apply when using the wrapper.

## Behavior

- HTTP MCP endpoint: `http://127.0.0.1:39749/mcp`
- Health endpoint: `http://127.0.0.1:39749/healthz`
- Public transport: stateful MCP Streamable HTTP.
- Backend transport: stdio child process.
- Queue capacity: 128 pending requests.
- Queue order: drain one MCP session completely before moving to the next session.
- Protocol passthrough: tools and prompts, including pagination, plus backend instructions.
- Child lifecycle: starts during the first public `initialize` so the wrapper can mirror
  backend instructions and exact tools/prompts capabilities; idle stop after 20 minutes.
- Timeout defaults: read 10 seconds, write 60 seconds, `index_repository` 5 minutes.
- Retry defaults: read-only allowlist gets up to 2 retries after child failure.
- Cancellation: queued requests are dropped; active backend calls intentionally run
  until they complete or hit their configured timeout.

`/healthz` returns HTTP 200 when the child is running or intentionally idle. It returns
HTTP 503 only when recent child crashes cross the crash-loop threshold.

## Configuration

The child command is required. The app refuses to start without it.

Configuration uses `appsettings.json` plus environment variable overrides. Important
environment variables:

```bash
Wrapper__Child__Command=~/.local/bin/codebase-memory-mcp
Wrapper__BindUrl=http://127.0.0.1:39749
Wrapper__QueueCapacity=128
```

## Install As User Service

Run:

```bash
./scripts/install-systemd.sh
```

The service listens on port `39749` by default. Set a different port during
installation with:

```bash
CODEBASE_MEMORY_WRAPPER_PORT=39750 ./scripts/install-systemd.sh
```

The script publishes the framework-dependent app to:

```text
~/.local/share/codebase-memory-wrapper/app
```

It creates:

```text
~/.local/share/codebase-memory-wrapper/codebase-memory-wrapper.env
~/.config/systemd/user/codebase-memory-wrapper.service
```

It autodetects `~/.local/bin/codebase-memory-mcp`. If it cannot find it, it asks for
the path, verifies it is executable, writes it to the env file, then runs:

```bash
systemctl --user enable codebase-memory-wrapper.service
systemctl --user restart codebase-memory-wrapper.service
```

Useful commands:

```bash
systemctl --user status codebase-memory-wrapper.service
journalctl --user -u codebase-memory-wrapper.service -f
curl http://127.0.0.1:39749/healthz
```

## Codex MCP Config

Update `~/.codex/config.toml` manually so Codex connects to the wrapper instead of
starting `codebase-memory-mcp` directly:

```toml
[mcp_servers.codebase-memory-mcp]
url = "http://127.0.0.1:39749/mcp"

[mcp_servers.codebase-memory-mcp.tools.get_architecture]
approval_mode = "approve"

[mcp_servers.codebase-memory-mcp.tools.index_status]
approval_mode = "approve"

[mcp_servers.codebase-memory-mcp.tools.search_graph]
approval_mode = "approve"

[mcp_servers.codebase-memory-mcp.tools.get_graph_schema]
approval_mode = "approve"

[mcp_servers.codebase-memory-mcp.tools.trace_path]
approval_mode = "approve"

[mcp_servers.codebase-memory-mcp.tools.get_code_snippet]
approval_mode = "approve"

[mcp_servers.codebase-memory-mcp.tools.index_repository]
approval_mode = "approve"

[mcp_servers.codebase-memory-mcp.tools.list_projects]
approval_mode = "approve"

[mcp_servers.codebase-memory-mcp.tools.query_graph]
approval_mode = "approve"

[mcp_servers.codebase-memory-mcp.tools.check_index_coverage]
approval_mode = "approve"

[mcp_servers.codebase-memory-mcp.tools.get_file_outline]
approval_mode = "approve"

[mcp_servers.codebase-memory-mcp.tools.compare_graphs]
approval_mode = "approve"
```

Tool definitions and results are discovered from the child rather than maintained
as a wrapper-specific list. New read tools still need an entry in
`Wrapper:Retry:ReadOnlyRetryTools` to recover automatically after a child crash.
`get_file_outline` and `compare_graphs` are included in both configuration defaults.

## Verification

Build:

```bash
dotnet build
```

Run locally without installing:

```bash
Wrapper__Child__Command=~/.local/bin/codebase-memory-mcp dotnet run
```

Then check:

```bash
curl http://127.0.0.1:39749/healthz
```

## License

This wrapper is licensed under the [MIT License](LICENSE).

The software is provided "AS IS", without warranty of any kind, express or implied.
See [LICENSE](LICENSE) for the full terms and limitation of liability. This license
applies to the wrapper; `codebase-memory-mcp` and other dependencies retain their
own licenses.
