# Google Play: listing, declarations and answers (draft)

Ready-to-paste material for the Play Console. **Nothing here has been submitted.** Facts come from `PROJECT_SPEC.md` and `AndroidManifest.xml`; check each form against what the Console currently asks.

## Privacy policy URL

`https://github.com/Russell-KV4S/NodeX/blob/main/docs/PRIVACY.md` (after this file is pushed). A plain public URL is accepted; move it to a web page of your own later if you prefer.

## Store listing

**App name** (30 max): `NodeX` (or `NodeX: AllStarLink Node`, 23)

**Short description** (80 max):
`An AllStarLink node for your phone. Link to repeaters and nets with no radio.` (77)

**Full description** (4000 max):

```
NodeX is a standalone AllStarLink node for your phone. Register your own node number, link out to repeaters, hubs and nets, listen, and (with an amateur radio licence) talk. No Asterisk server, no radio, no extra hardware.

• Link up to 8 nodes at once. One is the transmit node; the rest are listen-only with per-node mute
• Find-a-node search and favourites with your own names
• Push-to-talk on screen (hold or toggle), from a Bluetooth button, or from a headset key
• Keeps the link alive with the screen off and reconnects when you move between Wi-Fi and mobile data
• Listen-only mode: no callsign needed, nothing can transmit
• Optional station-ID reminder. NodeX never transmits for you
• Android Auto: browse and switch favourite nodes and listen from the car
• Open source, no ads, no tracking. Your node password stays in the Android Keystore

You need an AllStarLink account and node number. Transmitting needs a valid amateur radio licence, and you are responsible for identifying and for following the rules of the nodes you link. NodeX does not verify callsigns. Linked nodes may be connected to real radio transmitters.

NodeX is an independent project and is not affiliated with AllStarLink.
```

**Category:** Communication. **Tags:** Communication, Tools.
**Contact email / website:** the developer account's address / `https://github.com/Russell-KV4S/NodeX`.

## Graphics still needed
- App icon 512×512 PNG: from `branding/` (owner to confirm which export).
- Feature graphic 1024×500: not made.
- Phone screenshots (2–8, 16:9 or 9:16, 320–3840 px): see `docs/screenshots/` for the desktop-rendered set; real-phone shots are better.
- Android Auto: if the media-category listing is wanted, screenshots of the car UI (none exist; never run on a head unit).

## Foreground-service declarations

All run in one foreground service started when the user links a node. Describe it as a user-visible ongoing task (a notification is always shown).

| Type | Justification (paste) |
|---|---|
| Microphone | While the user holds push-to-talk, the service captures the microphone and sends it to the node the user linked. Capture runs only while the user keys the transmitter, and must continue if the screen turns off or the user switches apps. |
| Media playback | The app plays received radio audio from the linked nodes continuously, including with the screen off, and exposes media controls (mute, switch node) through a MediaSession and Android Auto. |
| Connected device | The user can pair a Bluetooth push-to-talk button or speaker-microphone; the service keeps that connection to receive button presses while the screen is off. |
| Special use | Maintaining a live, user-initiated network link (UDP/IAX2 session and AllStarLink registration) with the screen off and across Wi-Fi/mobile handoff. The manifest subtype text is: "Keeps the user's AllStarLink radio node registered and reachable while the app is in the background". No standard type covers a persistent peer-to-peer voice link. |

A short screen recording (link a node, lock the screen, audio keeps playing, notification visible) is the usual evidence Play asks for; I cannot record one from a phone.

## Data safety form

| Question | Answer |
|---|---|
| Collects or shares required data types? | Yes (see rows) |
| Data encrypted in transit? | Registration: yes (HTTPS). Voice/IAX2 to nodes: **no** (the AllStarLink protocol is unencrypted UDP). Say so honestly; answer "No" to "all data encrypted in transit" |
| Users can request deletion? | No data is held by the developer. State that in the policy (done) |
| Audio: voice or sound recordings | Collected: yes, not stored; sent to the linked node at the user's request; purpose App functionality; optional (listen-only needs none) |
| Personal info: name/user IDs | Callsign and node number: collected, shared with AllStarLink and the linked node; App functionality; required for transmitting |
| Device or other IDs / IP address | Public IP sent to AllStarLink and linked nodes as part of the protocol; App functionality |
| App info and performance (logs) | Stays on device unless the user shares it; not collected |
| Location, contacts, photos, files, financial, health | Not collected |
| Data sold? Used for ads? | No / No |

## Content rating questionnaire
Communication app with user-to-user voice (live, unmoderated through third-party nodes). Expect the "users can interact / share" answers to be yes; no violence, sexual content, gambling or purchases.

## Target audience
13+ or 18+ (an amateur-radio tool; not for children). Not in the Designed for Families programme.

## Release notes for the Play edition
- Car: browse, listen, mute and switch nodes. The Talk button and steering-wheel talk are **not** in the Play edition (the full edition on GitHub has them, off by default).
- Sideload and Play builds are signed differently and cannot be installed over each other.

## Honest risks
- Google may still reject a ham-link app in the media category; listen-only is the best case.
- Closed-test requirement (about 12 testers for 14 days) may apply before production on a newer personal account. Check Dashboard → "Production access".
- `REQUEST_IGNORE_BATTERY_OPTIMIZATIONS` is a restricted permission; Play may ask for a justification (user-chosen long-running link). If it causes trouble, remove it and keep the in-app guidance.
