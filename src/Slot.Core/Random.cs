using System.Security.Cryptography;

namespace Slot.Core;

public interface IRandomSource
{
    /// <summary>回傳 [0, exclusiveMax) 的均勻整數。</summary>
    int Next(int exclusiveMax);
}

/// <summary>正式使用的 RNG：作業系統的密碼學安全亂數（不使用 System.Random）。</summary>
public sealed class SecureRandomSource : IRandomSource
{
    public int Next(int exclusiveMax)
    {
        if (exclusiveMax <= 0) throw new ArgumentOutOfRangeException(nameof(exclusiveMax));
        return RandomNumberGenerator.GetInt32(exclusiveMax);
    }
}

/// <summary>
/// 只給測試與模擬器用的可重現 RNG（splitmix64 + 無偏差取範圍）。<b>不可</b>用於正式結果。
/// </summary>
public sealed class DeterministicRandomSource(ulong seed) : IRandomSource
{
    private ulong _state = seed;

    private uint NextUInt32()
    {
        _state += 0x9E3779B97F4A7C15UL;
        var z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return (uint)((z ^ (z >> 31)) >> 32);
    }

    public int Next(int exclusiveMax)
    {
        if (exclusiveMax <= 0) throw new ArgumentOutOfRangeException(nameof(exclusiveMax));
        // Lemire 的無偏差整數範圍法
        var range = (uint)exclusiveMax;
        var m = (ulong)NextUInt32() * range;
        var low = (uint)m;
        if (low < range)
        {
            var threshold = (uint)(-(int)range) % range;
            while (low < threshold) { m = (ulong)NextUInt32() * range; low = (uint)m; }
        }
        return (int)(m >> 32);
    }
}
