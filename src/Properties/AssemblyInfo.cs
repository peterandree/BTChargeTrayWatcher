// Assembly metadata for BTChargeTrayWatcher.
//
// The Win32 file/assembly version below is the app's single source of truth at
// runtime and is what WinGet uses to detect updates (`winget upgrade`). It MUST
// be kept in sync with the <Version> in BTChargeTrayWatcher.csproj and the
// `#define AppVersion` in installer/BTChargeTrayWatcher.iss.
//
// tools/build-installer.ps1 and tools/release-all.ps1 patch this file during a
// build/release, so you should not edit the version here by hand — run those
// scripts (or bump the version in the csproj and let release-all update this).

using System.Reflection;

[assembly: AssemblyTitle("BT Charge Tray Watcher")]
[assembly: AssemblyDescription("Windows tray app that monitors Bluetooth device battery levels and alerts on low or high thresholds.")]
[assembly: AssemblyCompany("Peterandree")]
[assembly: AssemblyProduct("BT Charge Tray Watcher")]
[assembly: AssemblyCopyright("Copyright (c) 2024 peterandree")]
[assembly: AssemblyVersion("3.2.0")]
[assembly: AssemblyFileVersion("3.2.0")]
[assembly: AssemblyInformationalVersion("3.2.0")]