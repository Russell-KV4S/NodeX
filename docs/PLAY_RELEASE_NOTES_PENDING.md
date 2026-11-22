# Pending Google Play release notes

Changes that have shipped on GitHub but **not yet on Google Play**. When the next Play release is made, paste the
block below into Play Console's release notes, then move the entry under "Play releases" and empty "Pending".

**Last Play release:** 1.25 (code 27), closed Alpha (sent for review), 2026-11-20.
**Bundle to upload next** is `NodeX-android-play.aab` from the latest GitHub release.

Rules:
- Play limit is **500 characters per language**; Re-count after every edit (`wc -m`).
- Each `<en-US>` tag goes on its own line or Play rejects the notes.
- Write for the **Play edition**: no Android Auto Talk / steering-wheel transmit (those are GitHub-only).
- Add a line here every time a release goes to GitHub while Play is held. If it no longer fits, merge or drop the smallest items.

## Pending

1.26 through 1.32 (code 35), pending Google Play upload (2026-11-22).

```
<en-US>
Added live RX/TX gain, auto-level, and Android speaker boost in the PTT panel and Settings.
Fixed IAX2 registration on cellular carrier NAT so calls and registration share one IP.
Replay local audio and combine clips into one shareable WAV.
Auto-connect to up to eight nodes; fixed Android startup auto-connect and audio stalls.
Follow the system theme or try four new themes; hide private hosts outside edit.
Fewer false receiving lights and reconnect/IPv6 fixes.
</en-US>
```

## Play releases

- 1.25 (code 27), 2026-11-20, closed Alpha only (sent for review with the switch to Google Group testers-nodex): private nodes and Private Nodes screen, bubble chart link, early-hangup message, fewer false receiving lights, no automatic mute, car radio option (covers 1.23-1.25).
- 1.22 (code 24), 2026-11-19: retry until you disconnect; IAX2 retried after an HTTPS fallback; Send logs newest-first.
