# Provider stream integrity

Gemini Interactions returns text deltas and may also return complete native steps in
the terminal interaction. Agentica compares the ordered text in those steps with
any text already streamed before publishing a completed response or native
continuation. A mismatch fails with `output_mismatch`; the runner cannot execute one
proposal and retain a different proposal as native history. If no text was streamed,
the complete terminal output supplies the response. The same comparison applies to
complete steps reconstructed from the stream. Thought summaries and signatures are
excluded from output text. See the [Interactions event and step contract](https://ai.google.dev/api/interactions-api-v1).

Anthropic usage values in `message_delta` are cumulative replacements. Agentica
updates the existing normalized input, output, and cached-input counters when a
field is supplied, including zero; omitted fields retain their most recent value.
This follows the [official Anthropic SDK stream accumulator](https://github.com/anthropics/anthropic-sdk-python/blob/main/src/anthropic/lib/streaming/_messages.py).
This change preserves the existing usage field mapping and does not add billing
categories or reinterpret cache-creation tokens.

HTTP stream fixtures cover contradictory Gemini output without a completed event,
ordered terminal output with and without text deltas, and cumulative Anthropic usage
updates with omitted and zero counts. No live account is required for these checks.
