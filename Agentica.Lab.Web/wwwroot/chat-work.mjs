// Read-only presentation over retained evidence. It never mutates raw events,
// canonical chat text, provider state, host acceptance or tool execution.
const field = (value, name) => value?.[name] ?? value?.[name[0].toUpperCase() + name.slice(1)];
const validIndex = value => Number.isSafeInteger(value) && value >= 0;
const terminal = status => !['queued', 'pending', 'running', 'active', 'starting', 'cancelling'].includes(String(status ?? '').toLowerCase());
export function readableWork(events, turnStatus = 'running') {
  const ordered = [...new Map(events.map(event => [event.sequence, event])).values()].sort((a, b) => a.sequence - b.sequence);
  const blocks = new Map(); const operations = new Map();
  const wireCalls = new Set(ordered.filter(event => event.type === 'provider.summary.delta' || event.type === 'provider.item').map(event => event.callId));
  for (const event of ordered) {
    const data = event.data ?? {}; let source; let lifecycle; let delta; let snapshot; let incomplete;
    if (event.type === 'provider.summary.delta') { source = data.source; lifecycle = 'Delta'; delta = data.text ?? ''; }
    else if (event.type === 'planner.stream' && !wireCalls.has(event.callId)) {
      if (data.kind !== 'ThoughtSummaryDelta' && !['ReasoningSummary', 1].includes(field(data.block, 'kind'))) continue;
      source = data.source; delta = data.kind === 'ThoughtSummaryDelta' ? data.text ?? '' : undefined;
      if (field(source, 'version') !== 1 && data.kind !== 'ThoughtSummaryDelta') continue;
      lifecycle = field(data.block, 'lifecycle') ?? 'Delta'; snapshot = field(data.block, 'textSnapshot'); incomplete = field(data.block, 'incomplete');
      if (field(source, 'version') !== 1) { lifecycle = 'Delta'; snapshot = undefined; incomplete = undefined; }
    } else if (event.type === 'provider.item' && /^response\.reasoning_summary_(part|text)\.(added|done)$/.test(data.type ?? '')) {
      source = { version: 1, itemId: data.item_id, outputIndex: data.output_index, summaryIndex: data.summary_index };
      lifecycle = data.type.endsWith('.added') ? 'Started' : data.type.includes('_text.') ? 'TextCompleted' : 'Completed';
      snapshot = data.part?.text ?? data.text; incomplete = data.status === 'incomplete';
    } else if (event.type === 'provider.item' && (data.item ?? data).type === 'reasoning') {
      const native = data.item ?? data;
      for (const [index, part] of (native.summary ?? []).entries()) {
        if (part.type !== 'summary_text' || typeof part.text !== 'string') continue;
        const itemId = native.id ?? event.itemId;
        const key = JSON.stringify([event.callId ?? '', itemId ?? '', index]);
        const block = blocks.get(key) ?? { key, callId: event.callId, itemId, index, firstSequence: event.sequence, delta: '', started: false, ended: false, reconstructed: !itemId };
        block.snapshot = part.text; block.snapshotOnly = true; block.lastSequence = event.sequence;
        blocks.set(key, block);
      }
      continue;
    } else {
      observeOperation(operations, event);
      continue;
    }
    const supportedSource = field(source, 'version') === 1;
    const itemId = (supportedSource ? field(source, 'itemId') : null) ?? event.itemId;
    const index = supportedSource ? field(source, 'summaryIndex') : null;
    const native = Boolean(itemId) && validIndex(index);
    const key = JSON.stringify([event.callId ?? '', itemId ?? '', native ? index : 'reconstructed']);
    const block = blocks.get(key) ?? { key, callId: event.callId, itemId, index: native ? index : null,
      firstSequence: event.sequence, delta: '', started: false, ended: false, reconstructed: !native };
    lifecycle = typeof lifecycle === 'number' ? ['Started', 'Delta', 'TextCompleted', 'Completed'][lifecycle] : lifecycle;
    if (lifecycle === 'Started') { block.started = true; block.initial = typeof snapshot === 'string' ? snapshot : ''; }
    if (typeof delta === 'string') block.delta += delta;
    if (typeof snapshot === 'string' && lifecycle !== 'Started') block.snapshot = snapshot;
    if (lifecycle === 'Completed') { block.ended = true; block.incomplete = incomplete; }
    block.lastSequence = event.sequence; blocks.set(key, block);
  }
  // A legacy delta lacks part indexes. Avoid duplicating it alongside a later
  // whole-item summary; retain the known exact delta grouping as reconstructed.
  const legacyItems = new Set([...blocks.values()].filter(block => block.reconstructed && block.delta).map(block => JSON.stringify([block.callId, block.itemId])));
  const result = [...blocks.values()].filter(block => !(block.snapshotOnly && legacyItems.has(JSON.stringify([block.callId, block.itemId]))));
  return {
    blocks: result.map(block => {
      const captured = (block.initial ?? '') + block.delta;
      const gap = typeof block.snapshot === 'string' && captured !== block.snapshot;
      return { ...block, capturedText: captured, text: block.snapshot ?? captured, gap,
        status: block.incomplete ? 'incomplete' : block.ended ? 'complete' : block.snapshotOnly ? 'snapshot'
          : terminal(turnStatus) ? 'end-not-observed' : 'streaming' };
    }).filter(block => block.text.trim()).sort((a, b) => a.firstSequence - b.firstSequence),
    operations: [...operations.values()].sort((a, b) => a.firstSequence - b.firstSequence)
  };
}

function observeOperation(operations, event) {
  const data = event.data ?? {};
  if (event.type === 'provider.web-search' && data.item?.id) {
    const item = data.item; const key = `search:${event.callId}:${item.id}`;
    const previous = operations.get(key);
    operations.set(key, { key, title: 'Web search', status: item.status ?? 'observed', preview: item.action?.query ?? '',
      firstSequence: previous?.firstSequence ?? event.sequence, lastSequence: event.sequence });
  }
  const execution = data.executionEvent;
  if (event.type !== 'harness.event' || !execution || !['step.started', 'receipt.emitted'].includes(execution.type)) return;
  const context = execution.context ?? {}; const id = context.stepId;
  const key = `tool:${context.runId ?? ''}:${id ?? event.sequence}`; const previous = operations.get(key);
  operations.set(key, { key, title: context.toolId ?? execution.source ?? 'Tool operation',
    status: execution.type === 'receipt.emitted' ? execution.data?.status ?? 'result received' : 'running',
    preview: execution.type === 'receipt.emitted' ? execution.payload?.message ?? '' : '',
    receiptId: context.receiptId, firstSequence: previous?.firstSequence ?? event.sequence, lastSequence: event.sequence });
}
