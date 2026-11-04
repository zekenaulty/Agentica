# OpenAI embedding seam

`OpenAiEmbeddingClient` implements the SDK-free core
`Agentica.Memory.IMemoryEmbeddingProvider`. The host supplies an immutable expected
`MemoryEmbeddingProfile` and its existing credential through
`OpenAiEmbeddingClientOptions`; the fallback is `OPENAI_API_KEY`. The client never
creates, writes or logs credentials. Supply a reusable `HttpClient` to intercept
or own transport lifetime. No embedding request is made merely by construction.

The entire profile ID must match, including provider, model, configuration,
dimensions and both document/query projection revisions. The host owns those
projection policies; Document and Query send the exact already-projected strings
without adapter prefixes. Configuration revision must identify the host's actual
endpoint/model/dimension-request policy. No default model is chosen here.

One call sends one POST to `/v1/embeddings`, requests float output, and optionally
sends dimensions. The response must identify the exact configured model and
contain one unique index/vector per input with the expected dimension, finite
components and nonzero norm. Returned vectors are ordered by input index and
detached/read-only. No automatic split, repair, retry, model substitution or
dimension fallback is performed. Omitting the dimensions parameter requires an
explicit `sendDimensions: false`; the expected returned dimension still applies.

The local bounds are 512 inputs, 32,768 UTF-16 units per text, 1 MiB aggregate
strict UTF-8 input, 8 MiB serialized request and a configurable response limit
(128 bytes–32 MiB). Count and actual enumeration are checked. These are resource
bounds, not an estimator or guarantee of provider token admission. The provider's
model/batch token limits still apply; no byte-to-token conversion is invented.

`MemoryEmbeddingBatch.InputTokens` is the optional raw `usage.prompt_tokens`
value. Missing, negative, noninteger or out-of-range usage remains null. Total
tokens, text length, usage arithmetic and prices do not stand in for input usage
or cost. HTTP/refusal/malformed responses produce sanitized exceptions without
remote content; caller cancellation and process-integrity failures propagate.

Official source checked on 2026-11-03:
[Create embeddings](https://developers.openai.com/api/reference/resources/embeddings/methods/create).
It defines input arrays, result indices, float encoding, usage and the dimensions
parameter (supported by text-embedding-3 and later models). The legacy
`text-embedding-ada-002` dimension option is therefore rejected unless explicitly
omitted. No live provider availability or semantic retrieval quality is claimed.

`SemanticEmbeddingClientTests` uses synthetic keys and intercepted HTTP only.
It tests exact ordering/profile/Unicode, immutable outputs, malformed vector
families, refusal/status behavior, usage unknowns, bounded streaming and
cancellation. Actual test execution and Lab credential/settings composition
remain with the coordinator; this source note is not a fresh live-test claim.
