# NodeX Google Cast TV Receiver Setup

NodeX supports two Chromecast modes out of the box on **Android** and **Desktop (Windows / Linux / macOS)**:

1. **Zero-configuration Default Media Receiver (`CC1AD845`)** — works immediately with **zero developer account** and **zero cost**. Leave `Cast App ID` blank in **Settings → Chromecast**: Chromecast plays live mixed node audio over LAN HTTP (`8 kHz` WAV) and displays a Now Playing card with your station label (`Station: KV4S · 523802 · NodeX Portable`), active linked nodes, and the NodeX logo.
2. **Custom TV Dashboard + Low-Latency Audio** (`docs/cast/index.html`) — displays the full 10-foot NodeX TV console (station callout, ASL registration indicator, live SPK/MIC bars, and up to 8 live dynamically sorted link cards with green RX / red TX borders) alongside Web Audio G.711 µ-law playback. Because Chromecast only runs a custom HTML5 receiver when registered with Google, this mode requires a one-time **\$5 Google Cast Developer Console** App ID.

---

## One-time setup for the full TV Dashboard

### 1. Host the receiver page over HTTPS (free with GitHub Pages)

1. Open your GitHub repository **Settings → Pages**.
2. Set **Source** to **Deploy from a branch**, branch **`main`**, folder **`/docs`**, and click **Save**.
3. Wait a minute for GitHub Pages to publish. Verify it loads at:
   - Live receiver: `https://russell-kv4s.github.io/NodeX/cast/`
   - Visual browser preview: `https://russell-kv4s.github.io/NodeX/cast/?demo=1`

### 2. Register the App ID in the Google Cast SDK Developer Console

1. Sign in at <https://cast.google.com/publish> (one-time **\$5 USD** registration fee from Google).
2. Click **Add New Application → Custom Receiver**.
3. Enter:
   - **Name:** `NodeX TV Console`
   - **Receiver Application URL:** `https://russell-kv4s.github.io/NodeX/cast/`
4. **Testing immediately without waiting for review:** Under **Cast Receiver Devices**, click **Add New Device** and enter your Chromecast's serial number (printed on the device or shown on its TV setup/ambient screen), then reboot your Chromecast once. Cast SDK registers the unpublished **8-character hex App ID** immediately for any device you add.
5. *(Optional)* Click **Publish** if you want every Chromecast (not just your registered test devices) to launch your App ID cleanly for all NodeX users.

### 3. Enter the App ID in NodeX

1. Open **NodeX → Settings → Chromecast**.
2. Paste your 8-character App ID (e.g. `A1B2C3D4`) into **Cast App ID**.
3. Save Settings, tap the **Cast** icon in NodeX's top bar, and select your Chromecast TV.
