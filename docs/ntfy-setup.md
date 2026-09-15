# Setting up ntfy.sh Integration in BTChargeTrayWatcher

This guide explains how to configure the ntfy.sh push notification feature.

---

## Prerequisites

- BTChargeTrayWatcher is running in the system tray.
- You have installed the ntfy app on your phone (see `ntfy-install-ios.md` or `ntfy-install-android.md`).

---

## Open the wizard

1. Left-click or right-click the BTChargeTrayWatcher tray icon.
2. Click **📱 Mobile notifications (ntfy.sh)…**

The setup wizard opens.

---

## Step 1 — Install the ntfy app

This step shows direct links and QR codes for the App Store and Play Store. If you have already installed ntfy on your phone, click **Next**.

---

## Step 2 — Choose a topic name

The topic is the channel identifier. Any ntfy client subscribed to the same topic will receive your alerts.

**Using the generated topic (recommended)**

- The wizard pre-fills a random topic such as `btwatcher-a1b2c3d4`.
- Click **Copy** to copy it to the clipboard.
- This random name makes it unlikely that anyone else is subscribed to the same topic.

**Typing your own topic**

- Clear the field and type your preferred name.
- Allowed characters: letters, digits, underscores, hyphens. Maximum 64 characters.
- The full URL is shown as a preview below the field.

> **Privacy notice:** Topics on the public ntfy.sh server are not password-protected. Anyone who knows your topic name can read the notifications. The generated random name mitigates this. Do not use a topic name that contains personal information.

**Custom server (optional)**

- If you run your own ntfy instance, replace `https://ntfy.sh` in the **Server** field with your server's URL.
- Leave unchanged to use the public server.

Click **Next** when the topic field is filled.

---

## Step 3 — Test and finish

1. Click **Send test notification**.
2. Check your phone — a notification titled **Test** should arrive within a few seconds.
3. If it arrives, click **Finish**. The feature is now enabled.
4. If it does not arrive within 30 seconds:
   - Confirm the topic in the ntfy app matches the one shown in the wizard (case-sensitive).
   - Check that your PC has internet access.
   - Verify the ntfy app has notification permission (see the install guides).

---

## Topic security (read this)

ntfy.sh topics are **unauthenticated shared secrets**: anyone who knows the topic can read every
notification and can also publish junk into it. That matters here because the alerts reveal when you
are at your desk and which devices you own.

- Treat the generated topic like a password. Do not paste it into chats, screenshots, or bug reports.
- It is stored in plain text in `settings.json` (`%LOCALAPPDATA%\BTChargeTrayWatcher\settings.json`).
  If the topic leaks, regenerate it (tray menu → **📱 Mobile notifications (ntfy.sh)…**) and resubscribe
  on every phone.
- The Options dialog warns about this above the topic field.

### Optional: private topics with an access token

ntfy supports authenticated topics (the names start with `$`). Those require an access token and can be
revoked without changing the topic:

1. Create an access token in the ntfy web app (ntfy.sh → *Account* → *Access tokens*) or in the Android app.
2. Open **Options → Notifications** and paste it into the **Token** field (masked).
3. Either keep the generated topic or rename it to a `$`-prefixed private topic, then run
   **Send ntfy test**.

The token is sent as `Authorization: Bearer <token>` on every publish. It is stored in `settings.json`
next to the topic and is never written to logs.

---

## What happens after setup

- Every time a Bluetooth device or your laptop battery crosses the configured low or high threshold, a push notification is sent to `https://ntfy.sh/{your-topic}`.
- The Windows Toast notification continues to fire alongside the mobile notification.
- You can re-open the wizard at any time to change the topic or disable the feature.

---

## Disabling mobile notifications

1. Open the tray menu → **📱 Mobile notifications (ntfy.sh)…**
2. On any step, click **Disable** or clear the topic field and click **Finish**.

The feature is disabled. No HTTP requests are sent until you re-enable it.
