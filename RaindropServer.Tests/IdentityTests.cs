using System.Security.Cryptography;
using RaindropServer.E2ee;
using RaindropServer.Security;

namespace RaindropServer.Tests;

public class Ed25519Tests
{
    // RFC 8032 §7.1, test 1: the empty message.
    private static readonly byte[] Rfc8032PublicKey =
        Convert.FromHexString("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
    private static readonly byte[] Rfc8032Signature = Convert.FromHexString(
        "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");

    [Fact]
    public void The_rfc_8032_vector_verifies() =>
        Assert.True(Ed25519Signatures.Verify(Rfc8032PublicKey, [], Rfc8032Signature));

    [Fact]
    public void A_changed_message_or_signature_does_not()
    {
        Assert.False(Ed25519Signatures.Verify(Rfc8032PublicKey, [1], Rfc8032Signature));
        var tampered = (byte[])Rfc8032Signature.Clone();
        tampered[0] ^= 1;
        Assert.False(Ed25519Signatures.Verify(Rfc8032PublicKey, [], tampered));
    }

    [Fact]
    public void Malformed_input_is_simply_invalid()
    {
        Assert.False(Ed25519Signatures.Verify(new byte[31], [], Rfc8032Signature));
        Assert.False(Ed25519Signatures.Verify(Rfc8032PublicKey, [], new byte[63]));
        Assert.False(Ed25519Signatures.Verify(new byte[32], [], new byte[64]));
    }
}

public class DeviceCertificateTests
{
    [Fact]
    public void A_certificate_from_the_rust_core_parses_and_verifies()
    {
        var certificate = DeviceCertificate.Parse(Fixtures.Load("certificate"));
        Assert.Equal(Fixtures.CertificateUserId, certificate.UserId);
        Assert.Equal(Fixtures.CertificateDeviceId, certificate.DeviceId);
        Assert.Equal(32, certificate.DeviceSignatureKey.Length);
        Assert.Equal(32, certificate.UserIdentityKey.Length);
        Assert.True(certificate.IsSignatureValid());
    }

    [Fact]
    public void Any_changed_byte_breaks_the_signature_or_the_parse()
    {
        var original = Fixtures.Load("certificate");
        for (var i = 0; i < original.Length; i++)
        {
            var tampered = (byte[])original.Clone();
            tampered[i] ^= 0x01;
            try
            {
                Assert.False(DeviceCertificate.Parse(tampered).IsSignatureValid(), $"byte {i}");
            }
            catch (ApiException e)
            {
                Assert.Equal("invalid_certificate", e.Code);
            }
        }
    }

    [Fact]
    public void Truncated_or_oversized_certificates_are_refused()
    {
        var original = Fixtures.Load("certificate");
        Assert.Throws<ApiException>(() => DeviceCertificate.Parse(original[..^1]));
        Assert.Throws<ApiException>(() => DeviceCertificate.Parse([.. original, 0]));
        Assert.Throws<ApiException>(() => DeviceCertificate.Parse(new byte[DeviceCertificate.MaximumLength + 1]));
        Assert.Throws<ApiException>(() => DeviceCertificate.Parse(null));
    }
}

public class ServerIdentityKeyTests
{
    [Fact]
    public void The_key_signs_the_tls_fingerprint_and_survives_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"raindrop-identity-{Guid.NewGuid():N}.key");
        using var first = ServerIdentityKey.LoadOrCreate(path);
        const string fingerprint = "AB:CD:EF:01";
        var signature = first.SignTlsBinding(fingerprint);

        Assert.True(ServerIdentityKey.VerifyTlsBinding(first.PublicKey, "abcdef01", signature));
        Assert.False(ServerIdentityKey.VerifyTlsBinding(first.PublicKey, "abcdef02", signature));

        using var reloaded = ServerIdentityKey.LoadOrCreate(path);
        Assert.Equal(first.PublicKey, reloaded.PublicKey);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        File.Delete(path);
    }

    [Fact]
    public void Another_key_does_not_vouch()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var point = ecdsa.ExportParameters(false).Q;
        byte[] other = [0x04, .. point.X!, .. point.Y!];

        var path = Path.Combine(Path.GetTempPath(), $"raindrop-identity-{Guid.NewGuid():N}.key");
        using var key = ServerIdentityKey.LoadOrCreate(path);
        Assert.False(ServerIdentityKey.VerifyTlsBinding(other, "abcdef01", key.SignTlsBinding("abcdef01")));
        File.Delete(path);
    }
}
