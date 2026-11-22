# Pending Google Play release notes

Changes that have shipped on GitHub but **not yet on Google Play**. When the next Play release is made, paste the
block below into Play Console's release notes, then move the entry under "Play releases" and empty "Pending".

**Last Play release:** 1.25 (code 27), closed Alpha (sent for review), 2026-11-20.
**Bundle to upload next:** the newest `keystore/nodex-<ver>-code<N>-play.aab` (1.25 is already on Play; next is whatever is newer).

Rules:
- Play limit is **500 characters per language**; Re-count after every edit (`wc -m`).
- Each `<en-US>` tag goes on its own line or Play rejects the notes.
- Write for the **Play edition**: no Android Auto Talk / steering-wheel transmit (those are GitHub-only).
- Add a line here every time a release goes to GitHub while Play is held. If it no longer fits, merge or drop the smallest items.

## Pending

1.26 (code 28), GitHub only so far (2026-11-21). Play-wording block, re-count with `wc -m` before pasting:

```
<en-US>
Fewer false "receiving" lights on busy hubs.
Safety: a failed dial never becomes your transmit node; car push-to-talk state ends when Android Auto unplugs.
Reconnect fixes: one call per node, rejected nodes aren't retried, auto-reconnect off stops retries.
Works on IPv6-only mobile data; background service survives Android restarts.
Settings Save keeps your registration choice; privacy notice updated.
</en-US>
```

## Play releases

- 1.25 (code 27), 2026-11-20, closed Alpha only (sent for review with the switch to Google Group testers-nodex): private nodes and Private Nodes screen, bubble chart link, early-hangup message, fewer false receiving lights, no automatic mute, car radio option (covers 1.23-1.25).
- 1.22 (code 24), 2026-11-19: retry until you disconnect; IAX2 retried after an HTTPS fallback; Send logs newest-first.
