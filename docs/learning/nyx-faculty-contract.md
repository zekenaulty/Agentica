# Historical-evidence faculty conformance — v1

Scope: Agentica [N-03 / #30](https://github.com/zekenaulty/Agentica/issues/30).
This is a disposable sample faculty and local conformance gate. It does not
change Nyx, Bridge, Cloud, a production MCP server/catalog or any user transcript.
The later independently operated historical-review seam is N-04 / #31.

## Installed contract

| Coordinate | Pin |
| --- | --- |
| Host logical server | `learning.history` |
| SDK server implementation | `agentica-historical-evidence-fixture`, `1.0.0` |
| Negotiated protocol in this fixture | `2026-07-28` |
| SDK package in the existing MCP project | `ModelContextProtocol.Core` `2.2.0` |
| Remote tool / host tool ID | `read_exact` / `history.read_exact` |
| Descriptor revision | `history.descriptor.v1` |
| Input / output revision | `history.read.input.v1` / `history.read.output.v1` |
| Input / output fingerprints | Canonical SHA-256 from the source-pinned JSON schemas in `HistoricalEvidenceContract` |
| Source identity | One immutable `ObligationInputManifest` with exactly one presented source cut |

The server uses the same low-level `McpServer` and
`StreamableHttpServerTransport` APIs as the existing runtime acceptance fixture.
The client is the existing `SdkMcpToolTransport`; discovery, protocol metadata,
tools/call and structured responses run through the actual SDK. The HTTP wrapper
only routes ephemeral loopback requests. The test asserts the negotiated
protocol, rather than inferring compatibility from the package name. Other
protocol versions/transports and production endpoint authentication remain
outside this gate. The SDK's local README and API XML at package revision 2.2.0
were inspected when composing the fixture.

The host-owned semantic declaration is:

- **Requires:** exact installed segment/source revision, whole UTF-16 cut,
  scope, perspective and matching current invocation scope.
- **Produces:** exact attributable evidence with declared coverage, or a typed
  unavailable/error result without evidence.
- **Applies:** one deliberately installed immutable historical cut, with no
  arbitrary source or cross-conversation lookup.
- **Effects:** read only. No source mutation, memory promotion, speech or domain
  effect occurs.

Those are sample metadata and existing descriptor/context-hint values. They are
not invented MCP permission fields or a universal SkillEnvelope implementation.
The logical server ID is a host identity label, not cryptographic authentication
of a remote implementation.

## Input, evidence and acceptance

The seven required arguments are segment ID, source ID/revision, scope,
perspective, start UTF-16 offset and UTF-16 length. Version 1 reads the entire
installed cut. It does not widen a cut, search a corpus, reconstruct missing
text or normalize Unicode. The immutable C-01 manifest supplies provenance,
coverage horizon, corpus revision and content identity. Its existing single-cut
character bound and the adapter's explicit result bound constrain output.

`HistoricalEvidenceFaculty.InspectAsync` reuses the MCP binding/readiness API.
It retains the same host binding object during dependency failure and adds a
host check before dispatch: requested cut and invocation scope must match the
installation. The native `ReadAsync` / `CallAsync` boundary repeats the cut check.
The server performs it again against its own installed snapshot. An out-of-scope
source, different revision/perspective, subrange, malformed input or unrelated
invocation scope therefore cannot initiate a source read. The host wrapper
rejects these before any tools/call request.

The in-process transport and SDK server call the same faculty and structured
projection. Both registrations use the existing `McpToolCatalogAdapter` for
receipt hashes, provenance, error status and receipt-linked observations.
Schema drift is rejected at inspection and at every later invocation. The
adapter also rechecks the host-labelled server before and after discovery.
The fixture advertises an extra tool but never binds it; a runner plan targeting
it fails before dispatch.

| Source disposition | Structured result | MCP receipt | Host completion |
| --- | --- | --- | --- |
| Exact cut, complete declared horizon | `Ready`, original exact evidence and coverage | Succeeded | Eligible only after the host verifies every acceptance field |
| Exact cut, partial declared horizon | `Ready`, exact evidence, partial coverage | Succeeded | Not accepted as complete |
| Unavailable connector | `Unavailable`, no evidence, unknown coverage | Succeeded | Not accepted |
| Reported source error | `Indeterminate`, no evidence, unknown coverage | Failed | Not accepted |
| Changed source/revision/attribution/text | `Indeterminate`, no substituted evidence, unknown coverage | Failed | Not accepted |
| Caller cancellation | No successful result returned to caller | No synthesized success receipt | Not accepted or retried implicitly |

An unavailable read can be a successful protocol operation. It does not satisfy
an obligation. Output schema/revision, content identity, exact source/revision,
attribution, requested range/perspective and complete declared coverage remain
host acceptance checks even after a successful tool receipt. A remote output
revision change cannot satisfy that acceptance. The current MCP adapter pins
the input schema; the output schema is advertised and source-pinned here, with
its semantic checks performed by the host rather than silently claimed as a
generic adapter guarantee.

## Focused qualification

`NyxFacultyMcpConformanceTests` compares the two transports through the real
`AgenticaRunner`, deterministic planning/validation and a separate completion
evaluator. This bounded faculty request needs one exact read; it does not
replace the multi-step comparative learning loop from C-02/N-02.

The conformance cases cover:

- Exact Unicode text, original absolute offsets, attribution, source and
  manifest identities, complete/partial coverage, unavailable and error meaning.
- Equivalent typed structured results; transport JSON escaping and receipt IDs
  are not content identity.
- Unbound discovery, source/scope/revision/perspective/range refusal, malformed
  input, schema drift and server relabelling before/during discovery.
- Binding-preserving SDK discovery failure, observed connection unavailability,
  shutdown and replacement of the actual SDK server on the same loopback endpoint,
  with a fresh session and the original schema pin/cut.
- Cancellation before dispatch and while an SDK read is pending. Server work may
  already have begun; caller cancellation is not proof that a source was never
  read. The operation is read only, and no hidden retry is introduced.
- Host rejection of a changed output revision, segment ID, source kind or
  attribution despite protocol completion and an echoed expected content hash.

The fixture bounds retained HTTP requests and request bodies. Its SSE is MCP
transport framing, not provider thinking. No provider invocation, reasoning
summary, private signature, measured model-quality result, cache saving or
production recovery is claimed.

Source is prepared for the focused gate; this document does not claim a test
pass before the coordinator runs it. The coordinator owns project registration,
lock generation, actual commands/results, exact-head review and main admission.
Run the focused `NyxFacultyMcpConformanceTests` plus the existing MCP gates when
qualifying this change. No production migration is required. Reverting the
sample/test slice must preserve other hosts' state and outstanding effects.
