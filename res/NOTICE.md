# Third-party assets

## Noto Sans (embedded subset)

The wordmark SVGs in this directory embed a subset of **Noto Sans Regular**
(glyphs `B`, `o`, `l`, `t`, `M`, `a`, `e`) as a base64 WOFF2 payload inside
each SVG's `<defs>` block, so the wordmark renders identically without a
system-installed font.

- Font: Noto Sans, Regular weight
- Copyright: © Google LLC
- License: SIL Open Font License, Version 1.1 — see [`OFL.txt`](./OFL.txt)
- Upstream: <https://fonts.google.com/noto/specimen/Noto+Sans>

The OFL permits embedding and redistribution. No modifications were made to
glyph outlines; only unused glyphs were removed via `fontTools.subset`.
