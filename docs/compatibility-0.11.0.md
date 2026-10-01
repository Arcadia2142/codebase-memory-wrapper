# Backend compatibility: 0.11.0

Verified on 2026-10-01 against the installed Linux binary and the live wrapper at
`http://127.0.0.1:39750/mcp`. Tests used a freshly indexed wrapper repository and
two disposable C# fixtures. This is a protocol/functionality check, not a benchmark.

- Backend version: `codebase-memory-mcp 0.11.0`.
- Binary SHA-256: `ce11c141431aeadd788506c3a7e6942db8fd438dec369d0707a39ec9fd8c6510`.
- Upstream release: <https://github.com/DeusData/codebase-memory-mcp/releases/tag/v0.11.0>.
- Baseline wrapper commit: `3a0f0c7`.
- SDK: `ModelContextProtocol` / `ModelContextProtocol.AspNetCore` `1.4.0`.

## Protocol contract

Direct stdio and HTTP wrapper advertised the same 17 tools. Complete definitions,
including descriptions, input schemas, annotations and ordering, were deeply equal.
All `tools/list` pages were read. The new tools are `get_file_outline` and
`compare_graphs`; no baseline tool was removed.

Server instructions, protocol version, tools/prompts capabilities, both prompt
definitions, and both `prompts/get` results were equal. The prompts remain
`explore_codebase` and `review_change_impact`. The backend currently advertises
`listChanged=false`; the wrapper forwards that value without overriding it.

Error and successful `tools/call` results, including `content`, `structuredContent`
and `isError`, matched directly and through the wrapper for seven Cypher probes.
Eight concurrent file-outline requests returned without errors.

The wrapper discovers tool messages dynamically. Only the two new read-tool names
needed adding to the retry allowlist, in both options and appsettings defaults.

## Functionality and continuation

| Probe | Observed result |
|---|---|
| `get_file_outline` | Exact-file declarations, stable offset paging, 11 total declarations in `McpProxyService.cs`. |
| `compare_graphs` | Bounded additions/removals, exact node/edge totals, per-set truncation; identical project arguments are rejected. |
| `query_graph` | Snapshot cursor paging: 15 classes across 8 pages, no duplicates. |
| `trace_path` | Snapshot cursor paging: 13 outbound dependencies across 7 pages, no duplicates. The old missing-cursor problem is fixed. |
| `get_code_snippet` | Full class source and paginated member outline with `next_member_offset`. |
| `detect_changes` | Changed-file cursor resumes the same diff; independent changed/impact/module continuation fields. |
| `search_code` | Result and directory paging plus phase timings; separate continuation fields. |
| `check_index_coverage` | Exact paths and path offset/limit; coverage and metadata freshness are preserved. |
| `get_graph_schema` | Paged label/edge counts and `diagnostics="full"` property lists. |
| `get_architecture` | JSON cycle analysis returned the repository's two-node disposal cycle. |
| `manage_adr(set_sections)` | Only the named fixture section changed; the other section stayed byte-identical. An identical retry produced identical content. |
| `ingest_traces` | Input accepted and counted; response explicitly says runtime edge creation is not implemented. |

Many tools now support `format="json"`, bounded output and multiple paging lanes.
Check the current schema and every lane's continuation fields. In particular,
coverage returned `path_has_more=true` while top-level `has_more=false`; relying on
the top-level flag alone would miss paths. Cursor arguments must preserve the
query/traversal context; totals with relation `gte` are lower bounds.

`compare_graphs` compares qualified identities without normalizing project-name
prefixes. In differently named fixture indexes, it reported every node and edge
as removed/added, including the unchanged `SharedFixture.Read`. Do not treat this
as a semantic source diff without inspecting those identities; `detect_changes`
is the appropriate tool for a Git change impact query.

## Cypher recheck

| Construct | 0.11.0 result |
|---|---|
| `WHERE toLower(...)` | Parser error. |
| `WHERE size(...)` | Unsupported function. |
| `WHERE 'Class' IN labels(n)` | Parser error. |
| `WHERE exists(n.name)` | Requires the `EXISTS { ... }` form instead. |
| `WHERE coalesce/substring/replace/left/right` | Correct matching rows. |
| Individual `RETURN toLower/coalesce/size/labels` | Correct scalar projections; combined separate projections also work. |
| Nested `RETURN toLower(coalesce(...))` | Strict parser rejects remaining input. |
| `CASE ... THEN n.name ...` | Returns literal `n.name`, not the property value. Scalar-literal branches work. |
| `count(n)` / `count(n) AS total` | Correct count, even with a small visible row limit. |
| `count(n) AS count` | Keyword alias rejected; use `total`. |
| Inline property maps, label alternation, `WITH`, `UNION` | Correct results. |
| `EXISTS { (n)-[:CALLS]->() }` | Correct relationship existence results. |
| Missing-name negative control | Zero rows, no parser error. |
| Unsupported trailing syntax | Explicit error instead of silently ignoring it. |

The unsupported/error cases above were reproduced directly, confirming backend
behavior rather than a wrapper translation problem. The global and installed
codebase-memory skills were updated, including removal of the old trace workaround.

## Recovery and build

The changed build passed `dotnet build --no-restore`. It was exercised on isolated
port `39751`: after terminating only its verified child, the same MCP session's
`get_file_outline` succeeded with a new child PID; `compare_graphs` also succeeded.
Health showed one recorded crash, no crash loop, and zero active/queued requests.
This tests recovery between requests, not failure during an active request.

The isolated wrapper and its children were stopped. Disposable fixture projects,
sources and audit artifacts were cleaned up. The live service was not redeployed;
the new retry entries require publishing/restarting the wrapper to activate them.
