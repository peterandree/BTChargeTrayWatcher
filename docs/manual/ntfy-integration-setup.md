# ntfy Integration Setup Guide — BTChargeTrayWatcher

This guide explains how to configure the ntfy push notification integration in BTChargeTrayWatcher so that battery alerts are also delivered to your phone.

## What this does
When enabled, BTChargeTrayWatcher sends battery alerts (low threshold, high threshold, critical battery) to the ntfy.sh public push notification service. Your phone receives these alerts via the ntfy app. Local Windows notifications continue to fire as normal.

## Before you start
- Install the ntfy app on your phone. See:
  - `docs/manual/ntfy-android-installation.md` for Android
  - `docs/manual/ntfy-ios-installation.md` for iOS

## Setup steps

### 1 — Generate a topic
1. Right-click the BTChargeTrayWatcher tray icon.
2. Open **Mobile notifications**.
3. Select **Generate topic**.
4. BTChargeTrayWatcher creates a random topic such as `btcw-8h2qv6m4k9r1x7p3`.
   - The topic is your shared secret. Do not share it publicly.
   - Anyone who knows the topic can receive or send notifications to it.

### 2 — Subscribe on your phone
1. Open the ntfy app on your phone.
2. Add a subscription:
   - Server: `ntfy.sh`
   - Topic: the value shown under **Mobile notifications → Show topic**
3. Allow notifications when prompted.

### 3 — Enable the integration
1. In the tray menu, open **Mobile notifications**.
2. Select **Enable ntfy notifications**.

### 4 — Send a test notification
1. Select **Send test notification**.
2. Confirm the notification appears on your phone.
3. If it does not arrive within 10 seconds, follow the troubleshooting steps below.

## Managing the integration

| Action | Menu path |
|--------|-----------|
| View current topic | Mobile notifications → Show topic |
| Regenerate topic | Mobile notifications → Regenerate topic |
| Disable integration | Mobile notifications → Disable ntfy notifications |
| Re-enable integration | Mobile notifications → Enable ntfy notifications |
| Send test alert | Mobile notifications → Send test notification |
| Android install guide | Mobile notifications → Android setup guide |
| iPhone install guide | Mobile notifications → iPhone setup guide |

> **After regenerating the topic:** you must resubscribe on every phone. The old topic receives no further messages.

## Notification payload
Notifications sent to ntfy.sh contain:
- **Title:** BTChargeTrayWatcher
- **Body:** device display name and battery percentage, e.g. `Headphones battery low: 12%`
- **Priority:** high for threshold events, urgent for critical battery

No MAC addresses, Windows usernames, or machine identifiers are included.

## Troubleshooting

**Test notification not received:**
1. Confirm the topic in the ntfy app matches the one shown in BTChargeTrayWatcher exactly.
2. Confirm notifications are allowed for ntfy on your phone.
3. Check that the Windows PC has internet access.
4. On Android with the Google Play flavor, enable instant delivery in ntfy settings.
5. Regenerate the topic and resubscribe if the issue persists.

**Alerts not arriving during normal use:**
1. Verify the integration is enabled (**Mobile notifications → Status**).
2. Verify ntfy is still subscribed to the correct topic on your phone.
3. Check for battery optimization restrictions on your phone (Android only).

**Privacy:**
- Topics on the public ntfy.sh server are unprotected. Anyone with the topic name can subscribe.
- Use the generated random topic. Do not replace it with a guessable name.
- Message content is limited to device name and battery percentage.
