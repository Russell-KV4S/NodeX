NodeX 1.25 lets you hear nodes over the car radio and stops muting nodes on its own.

**What changed in 1.25**
- **Hear nodes over the car radio (new option, off by default):** Settings → "Android Auto: hear nodes over the car radio". When the car is playing its own radio, satellite or another app, a node is played the way navigation prompts are: the car turns the radio down while someone talks and back up about 1.5 s after they stop. Muted nodes stay silent. When NodeX itself is playing in the car, nothing changes.
- **Nothing is muted unless you press Mute:** car or Bluetooth Pause/Stop, navigation prompts, calls and other apps no longer mute your nodes, and Play no longer unmutes them. (Android Auto and some stereos send Pause on their own, which used to leave everything muted.) Node audio now keeps playing during a phone call in the car.
- **Fewer false "receiving" lights:** silent audio from an idle hub no longer lights it green, and NodeX remembers which hubs announce their transmissions across reconnects.
- **Private link cards** show the node number and name without the host name.

**From 1.24**
- **Bubble chart:** each public node now has a "Bubble chart ↗" link on its linked-node card, in Find a node results, and in a favourite's right-click / long-press menu. It opens the node's network map on stats.allstarlink.org in your browser. Private nodes don't have one, because they aren't on the stats site.

**From 1.23: private nodes**
- **Private nodes:** type `1999@host` or `1999@host:4570` in the Connect box, or paste a line from rpt.conf `[nodes]` (`radio@host:port/1999`). Save it and it appears with your favourites, marked PRIVATE. NodeX dials that address directly, looks the host name up fresh on every dial (so dynamic DNS works), and never asks allstarlink.org about it. It always logs in as `radio` with no password, which is how app_rpt links work.
- **Private Nodes screen:** add, edit, remove and test (IAX POKE) private nodes. A help card shows the exact `[nodes]` line the server's owner needs to add if their server doesn't load the public node list, with a copy button.
- **Share links:** `nodex://add?node=1999&host=...` links open NodeX on Android and Windows and ask before saving. They never connect on their own. On Windows, clicking a link while NodeX is open hands it to the running copy.
- **Android Auto:** private nodes show in the car's favourites, can be found by voice, and "play NodeX" resumes the last one you used.
- If a private server accepts the call and then hangs up straight away, NodeX now tells you the server probably doesn't know your node and points to the rpt.conf fix.

Includes all 1.22 changes (retry until you disconnect).

Downloads:
- **Android:** `NodeX-android.apk` is the full sideload edition, including optional Android Auto Talk and steering-wheel PTT.
- **Windows:** extract the x64 or ARM64 ZIP and run `NodeX.UI.Desktop.exe`.
- **Linux:** extract the x64 or ARM64 tarball and run `./NodeX.UI.Desktop`. Install the distribution's PortAudio/JACK, font, X11 and secret-service dependencies as needed.
- **macOS:** extract the Apple Silicon or Intel ZIP and move `NodeX.app` to Applications. Ad-hoc signed, not notarized; macOS may need approval in Privacy & Security.
- **iOS:** excluded from this release pipeline.

**Switching from Google Play:** Play and sideload signatures differ. Export your settings before uninstalling the Play build; you may need to re-enter the node password.

**Not yet verified:** the car radio option and the mute change haven't been tried in a car; the radio option assumes the car tells the phone it has switched to the radio, and how it sounds depends on the car. The false-RX change hasn't been checked on a phone. The bubble chart link is build- and unit-tested only and hasn't been tapped on a phone yet. Private nodes are build- and unit-tested only. They haven't yet been dialled against a live private server, and share links, the phone screens and Android Auto haven't been tried on a device or in a car. IPv6 addresses are accepted but can't be dialled yet. macOS/Linux share-link registration is not built.
