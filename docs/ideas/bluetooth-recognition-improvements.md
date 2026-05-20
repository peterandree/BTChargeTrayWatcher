# Bluetooth Recognition Improvements: Battery-Friendly, Aligned Proposals

This document outlines proposals to improve Bluetooth device recognition in BTChargeTrayWatcher, focusing only on ideas that align with current design decisions and do not negatively impact device or host battery life.

## 1. Device Name Caching & Alias Resolution
- **Description:**
  - Enhance the existing alias system to better persist user-defined device names across re-pairings or address changes.
  - Use fuzzy matching or heuristics to associate new device addresses with previously known names.
- **Benefits:**
  - Improves user experience by reducing confusion from address changes.
  - No extra Bluetooth activity required.

## 2. Multi-Source Device Aggregation
- **Description:**
  - Further improve the DeviceAggregationPipeline to merge and deduplicate device information from GATT, Classic, and Windows paired device lists.
  - Use device address, name, and class for robust matching.
- **Benefits:**
  - Increases recognition accuracy without additional polling.
  - Fully within current architecture (Monitoring/ only).

## 3. Device Class/Type Filtering
- **Description:**
  - Extend filtering logic to exclude non-battery or irrelevant device classes (e.g., printers, legacy audio) from the device list.
- **Benefits:**
  - Reduces UI clutter and false positives.
  - No impact on device power usage.

## 4. Improved Error Handling & Logging
- **Description:**
  - Add more detailed logging for device discovery failures, permission issues, or API errors.
  - Surface actionable errors to the user for easier troubleshooting.
- **Benefits:**
  - Aids debugging and user support.
  - No effect on Bluetooth activity or battery.

## 5. User-Driven Device Discovery (Manual Scan)
- **Description:**
  - Enhance the existing manual scan feature to optionally perform a deeper scan when triggered by the user.
  - Ensure all deep scans are user-initiated, not backgrounded.
- **Benefits:**
  - Empowers users to resolve recognition issues on demand.
  - No ongoing battery impact; only runs when requested.

## 6. Fallback to Windows Device Enumeration (Passive)
- **Description:**
  - Use Windows.Devices.Enumeration as a passive source for device info, without actively connecting or polling devices.
  - Encapsulate in a new IBatteryReader implementation within Monitoring/.
- **Benefits:**
  - Broader device coverage, especially for BLE devices.
  - No extra device wakeups or power drain.

---

**Excluded:**
- Proposals involving frequent or aggressive polling, background scans, or active RSSI queries are omitted to avoid unnecessary battery drain.
- Telemetry/analytics is excluded as it is not currently in scope per design decisions.

**All proposals above:**
- Respect project boundaries (no WMI/WinRT outside Monitoring/, no settings persistence outside ThresholdSettings).
- Require new readers to implement IBatteryReader.
- Are safe for battery life and user privacy.
