using Svg;
using System.Drawing.Imaging;

string outputPath = args.Length > 0 ? args[0] : "app.ico";

const string svgContent = """
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

int[] sizes = [256, 48, 32, 16];

var pngDatas = new List<byte[]>();
foreach (int size in sizes)
{
    var doc = SvgDocument.FromSvg<SvgDocument>(svgContent);
    using var bmp = doc.Draw(size, size);
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    pngDatas.Add(ms.ToArray());
}

// Write ICO (PNG-inside-ICO, supported since Windows Vista)
using var file = new FileStream(outputPath, FileMode.Create);
using var w = new BinaryWriter(file);

// ICONDIR
w.Write((short)0); // reserved
w.Write((short)1); // type: ICO
w.Write((short)sizes.Length);

// ICONDIRENTRY × count
int offset = 6 + 16 * sizes.Length;
for (int i = 0; i < sizes.Length; i++)
{
    int s = sizes[i];
    w.Write((byte)(s >= 256 ? 0 : s)); // width  (0 = 256)
    w.Write((byte)(s >= 256 ? 0 : s)); // height (0 = 256)
    w.Write((byte)0);  // colorCount
    w.Write((byte)0);  // reserved
    w.Write((short)1); // planes
    w.Write((short)32); // bitCount
    w.Write((int)pngDatas[i].Length);
    w.Write((int)offset);
    offset += pngDatas[i].Length;
}

foreach (var png in pngDatas)
    w.Write(png);

Console.WriteLine($"Generated {outputPath} ({new FileInfo(outputPath).Length / 1024} KB, {sizes.Length} sizes)");
