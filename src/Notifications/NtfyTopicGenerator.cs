using System.Security.Cryptography;
using System.Text;

namespace BTChargeTrayWatcher;

/// <summary>
/// Generates cryptographically random ntfy topic names.
/// Topics are treated as shared secrets; they must be unguessable.
/// Format: btcw-[16 lowercase hex chars]  (e.g. btcw-3a9f1c7e2b04d58a)
/// </summary>
public static class NtfyTopicGenerator
{
    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);

        var sb = new StringBuilder("btcw-", 21);
        foreach (byte b in bytes)
            sb.Append(b.ToString("x2"));

        return sb.ToString();
    }
}
