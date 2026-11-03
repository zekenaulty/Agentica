# Local Markdown dependencies

The experimental chat uses Marked only as a GFM lexer. It constructs allowlisted
DOM nodes itself and never inserts parser-produced HTML. Raw HTML remains literal
text, images are links, and links accept only absolute HTTP(S) without userinfo.
The entities decode-only modules supply standards-based named/numeric character
reference decoding without passing untrusted strings to an HTML parser.

Versions were selected from npm's stable `latest` dist-tag on 3 November 2026:

- `marked@18.1.3` — MIT; upstream `markedjs/marked`.
- `entities@9.0.0` — BSD-2-Clause; upstream `fb55/entities`.

`markdown-provenance.json` records source tarballs, verified npm SHA512 integrity,
local byte lengths and SHA512 hashes. Distribution bytes are unchanged; Marked's
ESM entry is renamed from `.js` to `.mjs`. Runtime files are served locally and
make no CDN requests. Package licenses remain alongside their vendored modules.

For upgrades, query the registry, download with scripts disabled, verify the
tarball integrity before extracting, copy the explicit runtime import closure,
refresh provenance hashes, and run the JS plus browser Markdown qualifications.
Do not change dependency bytes without updating and reviewing those pins.

Upstream documentation: https://marked.js.org/using_pro and
https://github.com/fb55/entities .
