# NodeX privacy policy

*Last updated: 2026-11-12. Applies to NodeX on Android, Windows, Linux and macOS.*

NodeX is a free, open-source AllStarLink node app. It has no accounts, no advertising, no analytics and no telemetry. The developer runs no server that receives your data.

## What NodeX does with your data

**Stays on your device**
- Your settings (callsign, node number, favourites, audio options) are saved in the app's private storage.
- Your AllStarLink node password is stored only in the platform's secret store (the Android Keystore on Android). It is never written to the settings file or to the log.
- Android Auto Backup may copy the settings file (not the password) to your own Google account backup, as for any app. You can switch that off in Android settings.

**Sent over the network, only because you asked NodeX to connect**
- **AllStarLink registration** (`register.allstarlink.org`, HTTPS): your node number, node password and the public IP address your connection comes from. This is how an AllStarLink node tells the network where it is. It is governed by AllStarLink's own policies.
- **Looking up and linking nodes** (`stats.allstarlink.org`, DNS, and IAX2 over UDP to the node you link): the node numbers you search for or connect to, your node number (the IAX caller identity is `NodeX-<node number>`) and your public IP address. Your callsign is not sent. Other nodes on the network can see these, as with any AllStarLink node. To find which port a node listens on, NodeX asks your device's DNS servers and, when it cannot use them, the public resolvers 1.1.1.1 (Cloudflare) and 8.8.8.8 (Google), which then see the number of the node being looked up.
- **Find a node**: the text you type in the search box is sent to `stats.allstarlink.org`.
- **Your voice**: while you key the transmitter, microphone audio is sent to the node you chose, and through it possibly onto the air and to anyone linked to that node. NodeX never records audio. Received audio is played and discarded.

**Not collected:** your location, contacts, photos, files, advertising ID, or anything about other apps. Bluetooth permissions are used only to find and read a push-to-talk button you choose, and the "neverForLocation" flag is set.

**GitHub updates (full sideload Android edition only)**
- Opening the phone app checks the project's latest stable GitHub release at most once per day while the process stays running; Settings also has a manual check. GitHub receives your public IP address and a generic NodeX updater user-agent, but no node password, callsign, node number or GitHub credentials.
- An APK downloads only when you choose Download and install. It is kept in the app's private cache and shared temporarily with Android's package installer after verification. Android asks you to approve installation. The Play edition does not use this updater or request installation permission.
- Releases are currently private. The app contains no GitHub token and reports the update feed as unavailable until anonymous access is enabled. Authorized users can download releases directly through GitHub.

## Diagnostic log

NodeX keeps a small rolling log file on your device (node numbers, callsign, IP addresses, connection events; never the password). It is not sent anywhere. If you choose **Settings → Troubleshooting → Send logs**, Android's share sheet opens and you decide where it goes.

## Permissions (Android)

| Permission | Why |
|---|---|
| Microphone | Your voice, only while you key the transmitter (not needed in Listen-only mode) |
| Internet / network state / Wi-Fi state | Linking to nodes and reconnecting when your network changes |
| Notifications | The always-on notification shown while NodeX stays linked in the background |
| Foreground service, wake lock | Keep the link and audio alive with the screen off |
| Bluetooth (connect, scan) | Optional Bluetooth push-to-talk button or headset |
| Ignore battery optimisation (request only) | Optional, so Android does not stop a link you left running |
| Request package installs (full sideload edition only) | Open Android's installer for an update you choose; Android still requires your confirmation |

## Children

NodeX is for licensed and prospective amateur radio operators and is not directed at children.

## Your control

Delete the app to remove its data from your device. To stop your node being listed, stop NodeX (Exit) and, if you want it gone, delete the node from your AllStarLink account. NodeX holds no copy of your data elsewhere, so there is nothing for the developer to delete on your behalf.

## Changes and contact

Changes to this policy will be committed to this file in the project repository. Questions: open an issue at <https://github.com/Russell-KV4S/NodeX/issues>; for security problems use the private route in [SECURITY.md](../SECURITY.md).
