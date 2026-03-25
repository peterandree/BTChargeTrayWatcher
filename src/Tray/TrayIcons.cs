namespace BTChargeTrayWatcher;

internal static class TrayIcons
{
    internal const string Normal = """
        <svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
          <defs>
            <linearGradient id="batteryFill" x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stop-color="#7CFF7C"/>
              <stop offset="50%" stop-color="#2EDB2E"/>
              <stop offset="100%" stop-color="#0A8F0A"/>
            </linearGradient>
            <linearGradient id="metal" x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stop-color="#F2F2F2"/>
              <stop offset="100%" stop-color="#A0A0A0"/>
            </linearGradient>
          </defs>
          <rect x="78" y="52" width="100" height="148" rx="22"
                fill="url(#batteryFill)" stroke="#D0D0D0" stroke-width="4"/>
          <rect x="108" y="30" width="40" height="20" rx="8" fill="url(#metal)"/>
          <rect x="92" y="64" width="16" height="120" rx="8" fill="#FFFFFF" opacity="0.18"/>
          <ellipse cx="128" cy="200" rx="50" ry="12" fill="#00FF88" opacity="0.15"/>
        </svg>
        """;

    internal const string Alert = """
        <svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
          <defs>
            <linearGradient id="batteryFill" x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stop-color="#7CFF7C"/>
              <stop offset="50%" stop-color="#2EDB2E"/>
              <stop offset="100%" stop-color="#0A8F0A"/>
            </linearGradient>
            <linearGradient id="metal" x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stop-color="#F2F2F2"/>
              <stop offset="100%" stop-color="#A0A0A0"/>
            </linearGradient>
            <radialGradient id="alertGlow" cx="50%" cy="50%" r="50%">
              <stop offset="0%" stop-color="#FF6B6B"/>
              <stop offset="100%" stop-color="#B00020"/>
            </radialGradient>
          </defs>
          <rect x="78" y="52" width="100" height="148" rx="22"
                fill="url(#batteryFill)" stroke="#D0D0D0" stroke-width="4"/>
          <rect x="108" y="30" width="40" height="20" rx="8" fill="url(#metal)"/>
          <rect x="92" y="64" width="16" height="120" rx="8" fill="#FFFFFF" opacity="0.18"/>
          <ellipse cx="128" cy="200" rx="50" ry="12" fill="#00FF88" opacity="0.15"/>
          <g transform="translate(100,110)">
            <circle cx="58" cy="58" r="58" fill="url(#alertGlow)" stroke="#FFFFFF" stroke-width="5"/>
            <rect x="50" y="12" width="16" height="52" rx="7" fill="#FFFFFF"/>
            <circle cx="58" cy="80" r="10" fill="#FFFFFF"/>
          </g>
        </svg>
        """;
}
