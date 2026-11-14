# Installing NodeX on Android (GitHub edition)

There are two Android editions:

| Edition | Where from | Android Auto |
|---|---|---|
| **Google Play** | Play Store | Listen-only in the car |
| **Full (GitHub)** | `NodeX-android.apk` from [Releases](https://github.com/Russell-KV4S/NodeX/releases) | Adds the optional **Talk** button and steering-wheel PTT |

The GitHub APK is **sideloaded** (installed from a file, not the Play Store). That needs two one-time permissions on the phone: one to install the file, and one so Android Auto will show it.

## 1. Install the APK (sideloading)

1. On the phone, open [Releases](https://github.com/Russell-KV4S/NodeX/releases) in the browser and download `NodeX-android.apk` from the newest release.
2. Open the downloaded file (browser's download list, or the **Files** / **Downloads** app).
3. Android says it can't install apps from this source. Tap **Settings**, switch on **Allow from this source** for the app you opened the file from (Chrome, Files, etc.), then go back.
4. Tap **Install**. If Play Protect warns about an unknown developer, choose **Install anyway**.
5. Open NodeX and enter your AllStarLink node number and password.

Menu names vary by phone. On older Android versions the switch is **Settings → Security → Unknown sources**.

**Coming from the Play edition?** The two editions are signed with different keys, so Android will refuse to install over the top. Export your settings first (Settings → Move settings to another device), uninstall the Play version, install the APK, then import. You will need to re-enter your node password unless you exported it with a passphrase. Updates between GitHub versions keep your data.

## 2. Make it appear in Android Auto

Android Auto hides media apps that did not come from the Play Store. It has its own **Unknown sources** switch, separate from the one in step 1. You do not need to be connected to a car to set it.

1. Open **Android Auto** settings on the phone (search Settings for "Android Auto", or Settings → Connected devices → Connection preferences → Android Auto).
2. Scroll to the bottom and tap **Version** about 10 times. Allow developer settings when asked.
3. Tap **⋮** (top right) → **Developer settings**.
4. Scroll down the list and tick **Unknown sources**.
5. Unplug and reconnect to the car (or restart the Desktop Head Unit).
6. If NodeX isn't in the car's app launcher, go to Android Auto settings → **Customize launcher** and tick **NodeX**.

This setting may need turning on again after a large Android Auto update.

## 3. Turn on car Talk (full edition only)

In NodeX **Settings**, enable Talk from the car and choose a steering-wheel button if you want one. Both are off by default.

## Updating

Settings → **App updates** in the full edition checks the public releases feed. Android asks once for "Allow from this source" for NodeX itself, and always asks you to confirm the install. Updating is blocked while a node is linked or Android Auto is active.

## Troubleshooting

- **NodeX missing from the car launcher:** check Unknown sources in Android Auto developer settings (step 2.4), then Customize launcher.
- **"App not installed":** you probably have the Play edition installed. Uninstall it first (see above).
- **Can't find Unknown sources:** update Android Auto from the Play Store, then scroll further down the developer list.
