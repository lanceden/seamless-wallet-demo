using Wallet.Core.Security;

namespace Wallet.UnitTests;

public class SignatureVerifierTests
{
    private const string Secret = "demo-secret";
    private const string Ts = "1763440000";
    private const string Body = "{\"playerId\":\"p_1\"}";
    // openssl dgst -sha256 -hmac demo-secret，輸入 "1763440000.{...}"
    private const string KnownHex = "2a75affc42d252c5a65a75013fa8c4b76dbdda304fa5f9b851e25cabe56cb054";

    [Fact]
    public void Compute_matches_known_hmac_vector()
        => Assert.Equal(KnownHex, SignatureVerifier.Compute(Secret, Ts, Body));

    [Fact]
    public void Verify_accepts_valid_signature_case_insensitively()
    {
        var sig = SignatureVerifier.Compute(Secret, Ts, Body);
        Assert.True(SignatureVerifier.Verify(Secret, Ts, Body, sig));
        Assert.True(SignatureVerifier.Verify(Secret, Ts, Body, sig.ToUpperInvariant()));
    }

    [Theory]
    [InlineData("not-hex")]
    [InlineData("")]
    [InlineData("abcd")]
    public void Verify_rejects_malformed_or_wrong_signature(string sig)
        => Assert.False(SignatureVerifier.Verify(Secret, Ts, Body, sig));

    [Fact]
    public void Verify_rejects_tampered_body_or_timestamp()
    {
        var sig = SignatureVerifier.Compute(Secret, Ts, Body);
        Assert.False(SignatureVerifier.Verify(Secret, Ts, Body + " ", sig));
        Assert.False(SignatureVerifier.Verify(Secret, "1763440001", Body, sig));
        Assert.False(SignatureVerifier.Verify("other", Ts, Body, sig));
    }

    [Theory]
    [InlineData(1000, 1000, true)]
    [InlineData(1000, 1300, true)]
    [InlineData(1300, 1000, true)]
    [InlineData(1000, 1301, false)]
    [InlineData(1301, 1000, false)]
    public void IsTimestampFresh_uses_300_second_window(long ts, long now, bool expected)
        => Assert.Equal(expected, SignatureVerifier.IsTimestampFresh(ts, now));
}
