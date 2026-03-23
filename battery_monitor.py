#!/usr/bin/env python3
"""
Battery Charge Monitor
Monitors battery charge level and displays a notification when it reaches 80%
"""

import psutil
import time
from plyer import notification


def get_battery_percentage():
    """Get current battery percentage"""
    battery = psutil.sensors_battery()
    if battery is None:
        return None
    return battery.percent


def is_charging():
    """Check if battery is charging"""
    battery = psutil.sensors_battery()
    if battery is None:
        return False
    return battery.power_plugged


def show_notification(title, message):
    """Show desktop notification"""
    notification.notify(
        title=title,
        message=message,
        app_name="Battery Monitor",
        timeout=10
    )


def main():
    """Main monitoring loop"""
    print("Battery Monitor started. Press Ctrl+C to stop.")
    
    while True:
        try:
            battery_percent = get_battery_percentage()
            charging = is_charging()
            
            if battery_percent is not None:
                print(f"Battery: {battery_percent}% {'(Charging)' if charging else '(Discharging)'}")
                
                # Show notification when battery reaches 80% while charging
                if charging and battery_percent >= 80:
                    show_notification(
                        "Battery at 80%",
                        f"Battery charge level: {battery_percent}%"
                    )
                    print("Notification shown: Battery at 80%")
            
            time.sleep(60)  # Check every 60 seconds
            
        except KeyboardInterrupt:
            print("\nBattery Monitor stopped.")
            break
        except Exception as e:
            print(f"Error: {e}")
            time.sleep(10)  # Wait before retrying


if __name__ == "__main__":
    main()
