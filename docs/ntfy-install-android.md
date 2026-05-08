# Installing ntfy on Android

This guide is for users who want to receive BTChargeTrayWatcher battery alerts on an Android phone or tablet.

---

## Requirements

- Android 8.0 (Oreo) or later
- Google Play Store **or** F-Droid (for a fully open-source install without Google services)

---

## Option A — Google Play Store

### 1. Install

1. Open the **Play Store**.
2. Search for **ntfy**.
3. Tap **Install** next to the app by **Philipp Heckel**.

Direct link: `https://play.google.com/store/apps/details?id=io.heckel.ntfy`

### 2. Allow notifications

1. Open **ntfy**.
2. Tap **Allow** when prompted for notification permission.
3. If denied earlier: **Settings → Apps → ntfy → Notifications → Allow all**.

---

## Option B — F-Droid (no Google services)

1. Install F-Droid from `https://f-droid.org` if you have not already.
2. Open F-Droid and search **ntfy**.
3. Tap **Install**.
4. Follow the same notification permission steps above.

> When using F-Droid, push delivery uses ntfy's own WebSocket connection instead of Firebase Cloud Messaging. Battery consumption is slightly higher. For background delivery to work reliably, exempt ntfy from battery optimisation: **Settings → Battery → ntfy → Unrestricted**.

---

## Subscribe to your topic

1. In the ntfy app tap **＋** (bottom right).
2. Enter the topic name shown in the BTChargeTrayWatcher wizard, for example `btwatcher-a1b2c3d4`.
3. Leave **Server** as `https://ntfy.sh` unless you changed the server in the wizard.
4. Tap **Subscribe**.

---

## Verify

1. In BTChargeTrayWatcher, open the tray menu → **📱 Mobile notifications (ntfy.sh)…**
2. On the Test step, tap **Send test notification**.
3. Your phone should show a push notification within seconds.

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| No notification | Verify topic name matches exactly (case-sensitive) |
| Notification received in app but no heads-up | Settings → Apps → ntfy → Notifications → enable **Pop on screen** for the channel |
| Delayed delivery (F-Droid only) | Exempt ntfy from battery optimisation (see above) |
| Missing notifications after phone restart | Confirm ntfy is not killed by the system; enable **Autostart** if your ROM supports it |
