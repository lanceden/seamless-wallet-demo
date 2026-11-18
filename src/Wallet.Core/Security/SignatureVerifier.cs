using System.Security.Cryptography;
using System.Text;

namespace Wallet.Core.Security;

/// <summary>X-Signature = HEX(HMAC-SHA256(secret, timestamp + "." + rawBody))；時間窗 ±300 秒。</summary>
public static class SignatureVerifier
{
    public const int MaxSkewSeconds = 300;

    public static string Compute(string secret, string timestamp, string rawBody)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(timestamp + "." + rawBody))).ToLowerInvariant();
    }

    public static bool Verify(string secret, string timestamp, string rawBody, string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return false;
        byte[] provided;
        try { provided = Convert.FromHexString(signature); }
        catch (FormatException) { return false; }
        var expected = Convert.FromHexString(Compute(secret, timestamp, rawBody));
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    public static bool IsTimestampFresh(long timestamp, long nowUnixSeconds)
        => Math.Abs(nowUnixSeconds - timestamp) <= MaxSkewSeconds;
}
