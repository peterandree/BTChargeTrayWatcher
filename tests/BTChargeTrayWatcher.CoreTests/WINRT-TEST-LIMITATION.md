# WinRT Bluetooth API Test Limitation

**Current Limitation:**
- The .NET 10.x SDK does not support `TargetPlatformVersion` 10.0.22621.0 or higher, even with the Windows 10.0.22621.0 SDK installed.
- As a result, any code or tests that depend on WinRT Bluetooth APIs (e.g., `GattConnectionManager`) cannot be built or run in CI or on most developer machines until a future .NET SDK adds support.

**Workaround:**
- All WinRT Bluetooth-dependent tests are marked as `integration/manual only`.
- These tests must be run manually on a machine with a compatible .NET SDK (future version) and the correct Windows SDK.
- All other core logic and tests remain buildable and runnable in CI.

**Action Required:**
- When a compatible .NET SDK is released, update the project and re-enable these tests in CI.

---

_Last updated: 2026-05-11_
