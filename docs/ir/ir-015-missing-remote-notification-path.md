# IR-015 — Missing Remote Notification Path

## Status
Closed (Implemented)

## Resolution
Implemented on 2026-05-15. The repository now includes an ntfy-based remote notification integration that publishes battery alerts to the public `ntfy.sh` service. The integration was delivered in PR #55 ("feat: ntfy mobile push notifications") and is present in the main branch.

## Linked Work
- PR #55 (merged): feat: ntfy mobile push notifications (commit 37a35ac1)
- Files added/updated (selected):
	- `src/Notifications/NtfyNotificationChannel.cs`
	- `src/Notifications/NtfyTopicGenerator.cs`
	- `src/Notifications/NtfyIntegrationSettings.cs`
	- `src/Tray/NtfyMobileNotificationsMenuBuilder.cs`
	- `src/Settings/ThresholdSettings.cs` (ntfy settings hooks)
	- `src/Settings/SettingsPersistence.cs` (persisting ntfy settings)
	- `docs/manual/ntfy-integration-setup.md` and platform guides
	- Winget/manifest locale notes (2.0.0 mentions ntfy support)

## Notes
- Acceptance criteria from this IR (mobile setup, topic generation, test notification, and alerts published) have been implemented; verify end-to-end by enabling ntfy in the tray menu and sending a test notification to a phone subscribed to the generated topic.
- If you want the IR re-opened or sequenced into the backlog for additional improvements (e.g., private server support, authentication, analytics), re-open with details.

## Summary
BTChargeTrayWatcher has no mechanism to notify the user when they are away from the PC. All battery threshold alerts are delivered as Windows desktop toast notifications only. This means all alerts are silently missed when the user is not at their computer — the primary scenario for charging supervision.

## Impact
- High: a charged or critically discharged device goes unnoticed.
- Affects all users who leave devices charging unattended.

## Root Cause
No remote notification channel has been implemented. The current `NotificationService` is a single-strategy class tied to the Windows toast API with no extension point for additional channels.

## Violated Design Decisions
- ADR-014 (proposed): remote mobile notifications via ntfy.sh are the decided solution and have not been implemented.

## Required Changes
1. Add `INotificationChannel` interface.
2. Implement `WindowsToastNotificationChannel` (wraps current toast logic).
3. Implement `NtfyNotificationChannel` (HTTP POST to `https://ntfy.sh/{topic}`).
4. Add `NotificationDispatcher` to fan out to all enabled channels.
5. Add `NtfyIntegrationSettings` value object to persisted settings.
6. Add `NtfyTopicGenerator` producing cryptographically random topic strings.
7. Add tray menu submenu **Mobile notifications** with full setup flow.
8. Add **Send test notification** command.
9. Embed Android, iOS, and ntfy setup guides as in-app dialogs or markdown files.

## Acceptance Criteria
- [ ] User can open **Mobile notifications** from the tray menu.
- [ ] App generates a long random topic on first use.
- [ ] User can view the current topic.
- [ ] User can regenerate the topic.
- [ ] App sends test notification to ntfy.sh and reports success or failure.
- [ ] Android and iOS installation guides are accessible from the tray menu.
- [ ] ntfy setup guide is accessible from the tray menu.
- [ ] Battery low/high alerts are published to ntfy.sh when integration is enabled.
- [ ] Local Windows toast notifications continue to fire regardless of ntfy state.
- [ ] No sensitive data (MAC address, Windows username, machine name) is included in notification payloads.

## References
- ADR-014: `docs/adr/adr-014-remote-mobile-notifications-via-ntfy.md`
- ntfy publish API: https://docs.ntfy.sh/publish/
- ntfy phone subscription: https://docs.ntfy.sh/subscribe/phone/
