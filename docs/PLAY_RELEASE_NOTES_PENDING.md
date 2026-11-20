# Pending Google Play release notes

Changes that have shipped on GitHub but **not yet on Google Play**. When the next Play release is made, paste the
block below into Play Console's release notes, then move the entry under "Play releases" and empty "Pending".

**Last Play release:** 1.22 (code 24), internal testing and closed Alpha, 2026-11-19.
**Bundle to upload next:** the newest `keystore/nodex-<ver>-code<N>-play.aab` (currently `nodex-1.24-code26-play.aab`).

Rules:
- Play limit is **500 characters per language**; the text between the tags below is 442 now. Re-count after every edit (`wc -m`).
- Each `<en-US>` tag goes on its own line or Play rejects the notes.
- Write for the **Play edition**: no Android Auto Talk / steering-wheel transmit (those are GitHub-only).
- Add a line here every time a release goes to GitHub while Play is held. If it no longer fits, merge or drop the smallest items.

## Pending (1.23 + 1.24 + main)

```
<en-US>
- Private nodes: connect to nodes outside the AllStarLink directory by typing 1999@host or pasting a line from rpt.conf. Saved ones show with your favourites and in Android Auto.
- New Private Nodes screen to add, edit, test and share them.
- Bubble chart link on each node opens its network map on stats.allstarlink.org.
- If a private server hangs up at once, NodeX explains the rpt.conf fix.
- Fewer false "receiving" lights on idle hubs.
</en-US>
```

Included versions:
- **1.23 (code 25, 2026-11-20):** private and manually addressed nodes, Private Nodes screen, `nodex://add` share links, Android Auto private favourites, early-hangup notice.
- **1.24 (code 26, 2026-11-20):** bubble chart link per public node.
- **On main, not yet released:** false-RX hardening (last bullet) and private link cards without the host (too small to mention). The Play bundle must be built from a version that includes them, or drop that bullet.

## Play releases

- 1.22 (code 24), 2026-11-19: retry until you disconnect; IAX2 retried after an HTTPS fallback; Send logs newest-first.
