// Presentation only. Canonical source remains unchanged in Lab storage.
// Marked supplies GFM tokens; this module never consumes its HTML renderer.
import { Lexer } from './vendor/marked-18.1.3/marked.esm.mjs';
import { decodeHTMLStrict } from './vendor/entities-9.0.0/decode.js';
export const maximumMarkdownNodes = 10000;

export function safeMarkdownUrl(value) {
  if (typeof value !== 'string') return null;
  try {
    const url = new URL(decodeHTMLStrict(value));
    return ['http:', 'https:'].includes(url.protocol) && !url.username && !url.password ? url.href : null;
  } catch { return null; }
}

export function lexMarkdown(source) {
  const raw = String(source ?? ''); let text = raw; let boundaries = null;
  // Marked normalizes line endings. Preserve an exact UTF-16 boundary map for
  // citations; neither the original source nor its canonical offsets change.
  if (raw.includes('\r')) {
    const normalized = []; boundaries = [0];
    for (let index = 0; index < raw.length; index++) {
      if (raw[index] === '\r') { if (raw[index + 1] === '\n') index++; normalized.push('\n'); }
      else normalized.push(raw[index]);
      boundaries.push(index + 1);
    }
    text = normalized.join('');
  }
  const tokens = Lexer.lex(text, { gfm: true, breaks: false, pedantic: false });
  let cursor = 0; let mapped = true;
  const blocks = tokens.map(token => {
    const extent = token.raw ?? '';
    // Definition tokens can be absorbed into the lexer's reference map. Once
    // raw extents stop being contiguous, citations stay unplaced rather than
    // guessing offsets in repeated text or omitted reference definitions.
    mapped &&= text.slice(cursor, cursor + extent.length) === extent;
    const block = { token, start: mapped ? boundaries?.[cursor] ?? cursor : null, end: mapped ? boundaries?.[cursor + extent.length] ?? cursor + extent.length : null };
    cursor += extent.length; return block;
  });
  return { tokens, blocks };
}

/** citations: validated UTF-16 source offsets, e.g. {offset, source}.
 * createCitation receives that same object and returns a trusted DOM node.
 * Markers are siblings of rendered blocks, never children of code or links.
 */
export function renderMarkdown(container, source, { citations = [], createCitation } = {}) {
  const raw = String(source ?? ''); const document = container.ownerDocument;
  const result = { blockCount: 0, placedCitations: 0, unplacedCitations: 0, fallback: false };
  const pending = Array.isArray(citations) ? citations : [];
  let allocated = 0;
  const consumeNode = () => { if (allocated >= maximumMarkdownNodes) throw new RangeError('Markdown DOM budget exceeded.'); allocated++; };
  const text = value => { consumeNode(); return document.createTextNode(String(value ?? '')); };
  const element = (name, value) => { consumeNode(); const node = document.createElement(name); if (value !== undefined) node.append(text(value)); return node; };
  const fragmentNode = () => { consumeNode(); return document.createDocumentFragment(); };
  const decoded = value => decodeHTMLStrict(String(value ?? ''));
  const literal = token => text(token.raw ?? token.text ?? '');
  const maximumDepth = 32;
  function link(token, image, depth) {
    const href = safeMarkdownUrl(token.href); const label = decoded(token.text ?? (image ? 'Image' : token.href ?? ''));
    if (!href) return image ? text(`[Image: ${label}]`) : inline(token.tokens ?? [{ type: 'text', text: token.text ?? token.raw }], depth + 1);
    const anchor = element('a'); anchor.href = href; anchor.target = '_blank'; anchor.rel = 'noopener noreferrer';
    if (token.title) anchor.title = decoded(token.title);
    if (image) { anchor.className = 'markdown-image-link'; anchor.append(text(`[Image: ${label}]`)); }
    else anchor.append(inline(token.tokens ?? [{ type: 'text', text: token.text ?? href }], depth + 1));
    return anchor;
  }
  function checkbox(token) { const input = element('input'); input.type = 'checkbox'; input.disabled = true; input.checked = Boolean(token.checked); input.setAttribute('aria-label', token.checked ? 'Completed task' : 'Incomplete task'); return input; }
  function inline(tokens, depth = 0) {
    const fragment = fragmentNode();
    for (const token of tokens ?? []) {
      if (depth > maximumDepth) { fragment.append(literal(token)); continue; }
      let node;
      switch (token.type) {
        case 'strong': case 'em': case 'del':
          node = element(token.type); node.append(inline(token.tokens, depth + 1)); break;
        case 'codespan': node = element('code', token.text ?? ''); break;
        case 'br': node = element('br'); break;
        case 'link': node = link(token, false, depth); break;
        case 'image': node = link(token, true, depth); break;
        case 'checkbox': node = checkbox(token); break;
        case 'html': case 'escape': node = literal({ text: token.text, raw: token.text }); break;
        case 'text': node = token.tokens ? inline(token.tokens, depth + 1) : text(token.escaped ? token.text : decoded(token.text)); break;
        default: node = literal(token);
      }
      fragment.append(node);
    }
    return fragment;
  }
  function blocks(tokens, depth = 0, tight = false) {
    const fragment = fragmentNode();
    for (const token of tokens ?? []) {
      if (depth > maximumDepth) { fragment.append(literal(token)); continue; }
      let node;
      switch (token.type) {
        case 'space': case 'def': continue;
        case 'heading': node = element(`h${Math.max(1, Math.min(6, token.depth ?? 1))}`); node.append(inline(token.tokens, depth + 1)); break;
        case 'paragraph': case 'text':
          node = tight ? fragmentNode() : element('p'); node.append(inline(token.tokens ?? [{ type: 'text', text: token.text }], depth + 1)); break;
        case 'blockquote': node = element('blockquote'); node.append(blocks(token.tokens, depth + 1)); break;
        case 'code': {
          node = element('pre'); const code = element('code', token.text ?? '');
          const language = String(token.lang ?? '').split(/\s/, 1)[0]; if (/^[a-z0-9_+-]{1,40}$/i.test(language)) code.dataset.language = language;
          node.append(code); break;
        }
        case 'html': node = element('pre', token.raw ?? token.text ?? ''); node.className = 'markdown-raw-html'; break;
        case 'hr': node = element('hr'); break;
        case 'list':
          node = element(token.ordered ? 'ol' : 'ul'); if (token.ordered && Number.isSafeInteger(token.start) && token.start !== 1) node.start = token.start;
          for (const item of token.items ?? []) { const li = element('li'); if (item.task) li.className = 'markdown-task'; li.append(blocks(item.tokens, depth + 1, !token.loose)); node.append(li); } break;
        case 'checkbox': node = checkbox(token); break;
        case 'table': {
          node = element('table'); const head = element('thead'); const row = element('tr');
          const cell = (value, index, header) => { const td = element(header ? 'th' : 'td'); if (header) td.scope = 'col'; if (['left', 'right', 'center'].includes(token.align?.[index])) td.style.textAlign = token.align[index]; td.append(inline(value.tokens ?? [{ type: 'text', text: value.text }], depth + 1)); return td; };
          for (const [index, value] of (token.header ?? []).entries()) row.append(cell(value, index, true)); head.append(row); node.append(head);
          const body = element('tbody'); for (const values of token.rows ?? []) { const tr = element('tr'); for (const [index, value] of values.entries()) tr.append(cell(value, index, false)); body.append(tr); } node.append(body); break;
        }
        default: node = literal(token);
      }
      fragment.append(node);
    }
    return fragment;
  }
  try {
    const parsed = lexMarkdown(raw); const fragment = fragmentNode(); const rendered = [];
    for (const block of parsed.blocks) {
      if (['space', 'def'].includes(block.token.type)) continue;
      const wrapper = element('div'); wrapper.className = 'markdown-block'; wrapper.append(blocks([block.token])); fragment.append(wrapper); rendered.push({ ...block, wrapper });
    }
    result.blockCount = rendered.length;
    for (const citation of pending) {
      const offset = citation?.offset;
      const block = Number.isSafeInteger(offset) && offset >= 0 && offset <= raw.length ? rendered.find(value => value.start !== null && offset > value.start && offset <= value.end) : null;
      if (!block || ['code', 'html', 'hr'].includes(block.token.type) || typeof createCitation !== 'function') { result.unplacedCitations++; continue; }
      const marker = createCitation(citation);
      if (!marker || marker.ownerDocument !== document || typeof marker.nodeType !== 'number') { result.unplacedCitations++; continue; }
      const markerNodes = [marker]; while (markerNodes.length) { const node = markerNodes.pop(); consumeNode(); markerNodes.push(...(node.childNodes ?? [])); }
      const holder = element('span'); holder.className = 'markdown-block-citations'; holder.append(marker); block.wrapper.append(holder); result.placedCitations++;
    }
    container.replaceChildren(fragment); container.classList.add('markdown-content'); container.classList.remove('markdown-fallback'); return result;
  } catch {
    // Malformed or excessively nested streaming input remains safely readable.
    container.textContent = raw; container.classList.add('markdown-content', 'markdown-fallback');
    return { ...result, placedCitations: 0, unplacedCitations: pending.length, fallback: true };
  }
}
