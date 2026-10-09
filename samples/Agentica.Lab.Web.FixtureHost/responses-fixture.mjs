import { createServer } from 'node:http';
import { setTimeout as delay } from 'node:timers/promises';

// Scripted inventory plans only: no model calls and no authoritative host state.
const portIndex = process.argv.indexOf('--port');
const port = portIndex < 0 ? 5081 : Number(process.argv[portIndex + 1]);
if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error('Use --port with an integer from 1 to 65535.');
let call = 0;
const event = (response, type, data) => response.write('event: ' + type + '\ndata: ' + JSON.stringify(data) + '\n\n');
const server = createServer(async (request, response) => {
  try {
    if (request.method !== 'POST' || request.url !== '/v1/responses') {
      response.writeHead(404).end(); return;
    }
    const chunks = []; let size = 0;
    for await (const chunk of request) {
      size += chunk.length;
      if (size > 1024 * 1024) { response.writeHead(413).end(); return; }
      chunks.push(chunk);
    }
    const body = JSON.parse(Buffer.concat(chunks).toString('utf8'));
    if (body.model !== 'fixture-model' || body.stream !== true || body.store !== false) {
      response.writeHead(400).end('Expected fixture-model, stream=true, store=false.'); return;
    }
    // Replayed history may contain earlier prompts; inspect only the newest user message.
    const latest = body.input?.filter(item => item.role === 'user').at(-1)?.content;
    if (typeof latest !== 'string') { response.writeHead(400).end('Expected a user planning prompt.'); return; }
    const refinement = latest.includes('"refinedPlan"');
    const number = ++call;
    const plan = {
      planId: 'fixture-plan-' + number, description: 'Scripted loopback inventory qualification.',
      steps: [{
        stepId: 'fixture-step-' + number,
        toolId: refinement ? 'demo.accept' : 'demo.inspect',
        kind: refinement ? 'action' : 'query',
        effect: refinement ? 'writesLocalState' : 'readOnly',
        input: refinement ? { itemId: 'sample-1' } : {},
        reason: refinement ? 'Request acceptance and require the host receipt.' : 'Inspect the host-owned inventory.'
      }]
    };
    const text = JSON.stringify(refinement ? { reason: 'observation', refinedPlan: plan } : plan);
    const responseId = 'fixture-response-' + number;
    response.writeHead(200, { 'content-type': 'text/event-stream', 'cache-control': 'no-cache' });
    response.flushHeaders();
    event(response, 'response.created', { type: 'response.created', response: { id: responseId } });
    for (let offset = 0; offset < text.length; offset += 96) {
      await delay(40);
      if (response.destroyed) return;
      event(response, 'response.output_text.delta', { type: 'response.output_text.delta', delta: text.slice(offset, offset + 96) });
    }
    event(response, 'response.completed', {
      type: 'response.completed', response: {
        id: responseId, status: 'completed', output: [{
          id: 'fixture-message-' + number, type: 'message', role: 'assistant', status: 'completed',
          content: [{ type: 'output_text', text, annotations: [] }]
        }]
      }
    });
    response.end();
  } catch {
    if (!response.headersSent) response.writeHead(400);
    response.end();
  }
});
server.listen(port, '127.0.0.1', () => {
  console.log('Scripted Responses fixture: http://127.0.0.1:' + port + '/v1/responses (no model calls)');
});
