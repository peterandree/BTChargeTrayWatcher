# Installing ntfy on iOS

This guide is for users who want to receive BTChargeTrayWatcher battery alerts on an iPhone or iPad.

---

## Requirements

- iPhone or iPad running iOS 16 or later
- Free Apple ID

---

## Steps

### 1. Install the ntfy app

1. Open the **App Store** on your iPhone or iPad.
2. Search for **ntfy**.
3. Tap **Get** next to the app published by **Philipp Heckel**.
4. Authenticate with Face ID, Touch ID, or your Apple ID password.
5. Wait for the download to complete.

Direct link: `https://apps.apple.com/app/ntfy/id1625396347`

---

### 2. Allow notifications

1. Open the **ntfy** app.
2. When prompted, tap **Allow** to permit notifications.
3. If the prompt does not appear, go to **Settings → Notifications → ntfy** and enable **Allow Notifications** manually.

---

### 3. Subscribe to your topic

You will need the topic name from the BTChargeTrayWatcher setup wizard (see `ntfy-setup.md`).

1. In the ntfy app, tap **＋** (bottom right).
2. In **Topic name**, enter the topic you configured in BTChargeTrayWatcher, for example `btwatcher-a1b2c3d4`.
3. Leave **Server** as `https://ntfy.sh` unless you changed the server in the wizard.
4. Tap **Subscribe**.

---

### 4. Verify

1. In BTChargeTrayWatcher, open the tray menu → **📱 Mobile notifications (ntfy.sh)…**
2. On the Test step, tap **Send test notification**.
3. Your iPhone should show a push notification within a few seconds.

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| No notification received | Confirm the topic name in the ntfy app exactly matches the one in the wizard (case-sensitive) |
| Notifications arrive but silently | Go to Settings → Notifications → ntfy → enable **Sounds** |
| App shows "Connection error" | Check that the phone has internet access |
