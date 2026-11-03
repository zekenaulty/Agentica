import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { lexMarkdown, maximumMarkdownNodes, renderMarkdown, safeMarkdownUrl } from '../Agentica.Lab.Web/wwwroot/chat-markdown.mjs';

// Bounded DOM contract stub, deliberately without HTML parsing or a browser.
function testDocument() {
  const document = { allocations: 0 };
  class TestNode {
    constructor(type, name, value = '') { this.nodeType = type; this.nodeName = name; this.value = value; this.ownerDocument = document; this.childNodes = []; this.style = {}; this.dataset = {}; this.classes = new Set(); this.classList = { add: (...names) => names.forEach(name => this.classes.add(name)), remove: (...names) => names.forEach(name => this.classes.delete(name)), contains: name => this.classes.has(name) }; }
    append(...nodes) { for (const node of nodes) { if (node.nodeType === 11) { this.append(...node.childNodes); node.childNodes = []; } else this.childNodes.push(node); } }
    replaceChildren(...nodes) { this.childNodes = []; this.append(...nodes); }
    setAttribute(name, value) { this[name] = value; }
    get textContent() { return this.nodeType === 3 ? this.value : this.childNodes.map(node => node.textContent).join(''); }
    set textContent(value) { if (this.nodeType === 3) this.value = String(value); else this.replaceChildren(...(String(value) ? [document.createTextNode(String(value))] : [])); }
  }
  document.createElement = name => { document.allocations++; return new TestNode(1, name.toUpperCase()); };
  document.createTextNode = value => { document.allocations++; return new TestNode(3, '#text', value); };
  document.createDocumentFragment = () => { document.allocations++; return new TestNode(11, '#document-fragment'); };
  return document;
}

test('large valid GFM tables fall back to exact literal text within a bounded allocation budget and recover', () => {
  const document = testDocument(); const container = document.createElement('div'); document.allocations = 0;
  const source = '| Left | Right |\n| --- | --- |\n' + '| value | value |\n'.repeat(12000);
  assert.ok(source.length > 200000);
  const large = renderMarkdown(container, source);
  assert.equal(large.fallback, true); assert.equal(container.textContent, source); assert.equal(container.childNodes.length, 1);
  assert.equal(container.classList.contains('markdown-fallback'), true);
  assert.ok(document.allocations <= maximumMarkdownNodes + 1, 'At most the fixed renderer budget plus one exact fallback text node is allocated.');
  const normal = renderMarkdown(container, '# Recovered\n\n**Readable** again.');
  assert.equal(normal.fallback, false); assert.equal(container.classList.contains('markdown-fallback'), false);
  assert.equal(container.childNodes[0].childNodes[0].nodeName, 'H1'); assert.match(container.textContent, /RecoveredReadable again/);
});

test('local mature lexer recognizes GFM blocks and incomplete streaming fences', () => {
  const source = '# Heading\n\nA **strong** and *emphasis* with ~~removed~~.\n\n- [x] Complete\n- [ ] Pending\n\n> Quote\n\n| Left | Right |\n| :--- | ---: |\n| A | B |\n\n```js\nconst text = "<script>literal</script>";';
  const parsed = lexMarkdown(source);
  assert.deepEqual(parsed.tokens.filter(token => token.type !== 'space').map(token => token.type), ['heading', 'paragraph', 'list', 'blockquote', 'table', 'code']);
  const paragraph = parsed.tokens.find(token => token.type === 'paragraph');
  for (const type of ['strong', 'em', 'del']) assert.ok(paragraph.tokens.some(token => token.type === type));
  const list = parsed.tokens.find(token => token.type === 'list'); assert.equal(list.items[0].checked, true); assert.equal(list.items[1].checked, false);
  assert.equal(parsed.tokens.at(-1).text, 'const text = "<script>literal</script>";');
  assert.equal(source.endsWith(';'), true, 'Rendering helpers do not rewrite the raw source.');
});

test('Markdown URLs decode entities and allow only absolute HTTP(S) without userinfo', () => {
  assert.equal(safeMarkdownUrl('https://example.com/?a=1&amp;b=2'), 'https://example.com/?a=1&b=2');
  assert.equal(safeMarkdownUrl('&#104;ttps://example.com/ok'), 'https://example.com/ok');
  for (const value of ['javascript:alert(1)', 'jav&#97;script:alert(1)', 'java\tscript:alert(1)', 'data:image/svg+xml,test', 'file:///local', 'mailto:person@example.com', '/relative', '//example.com', null, '']) assert.equal(safeMarkdownUrl(value), null);
  const userInfo = new URL('https://example.com/'); userInfo.username = 'fixture'; assert.equal(safeMarkdownUrl(userInfo.href), null);
});

test('citation block extents preserve original UTF-16 CRLF coordinates and reference definitions', () => {
  const source = '# Title\r\n\r\nA 🧭 paragraph.\r\n\r\n```txt\r\nliteral\r\n```';
  const parsed = lexMarkdown(source); const paragraph = parsed.blocks.find(block => block.token.type === 'paragraph');
  assert.equal(paragraph.start, source.indexOf('A 🧭')); assert.equal(source.slice(paragraph.start, paragraph.end), 'A 🧭 paragraph.');
  const code = parsed.blocks.find(block => block.token.type === 'code'); assert.equal(code.start, source.indexOf('```'));
  const references = lexMarkdown('[ref]: https://example.com\n\nA [link][ref].');
  assert.equal(references.blocks.find(block => block.token.type === 'paragraph').start, 28, 'Non-rendered reference definitions still consume their exact source extent.');
});

test('pinned local Markdown dependencies match provenance SHA512 bytes', async () => {
  const root = new URL('../Agentica.Lab.Web/wwwroot/vendor/', import.meta.url);
  const provenance = JSON.parse(await readFile(new URL('markdown-provenance.json', root), 'utf8'));
  assert.deepEqual(provenance.dependencies.map(dependency => `${dependency.name}@${dependency.version}`).sort(), ['entities@9.0.0', 'marked@18.1.3']);
  for (const dependency of provenance.dependencies) for (const entry of dependency.files) {
    const bytes = await readFile(new URL(entry.path, root)); assert.equal(bytes.length, entry.bytes, entry.path);
    assert.equal('sha512-' + createHash('sha512').update(bytes).digest('base64'), entry.integrity, entry.path);
  }
  const renderer = await readFile(new URL('../Agentica.Lab.Web/wwwroot/chat-markdown.mjs', import.meta.url), 'utf8');
  assert.doesNotMatch(renderer, /\.innerHTML|\.outerHTML|insertAdjacentHTML|DOMParser|createElement\(['"](?:img|script|iframe)['"]\)/);
});
