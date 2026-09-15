using System.Security.Cryptography;

namespace NetPaw.Repos;

/// <summary>
/// ECDSA P-256 / SHA-256 over the canonical entries array. Chosen over Ed25519 because the .NET
/// BCL ships P-256 on every OS and Ed25519 would need a third-party package. Keys are exported as
/// base64 SubjectPublicKeyInfo (public) and PKCS#8 PEM (private).
/// </summary>
public static class PackSigner
{
    public const string Alg = "ecdsa-p256-sha256";

    public sealed record KeyPair(string PrivateKeyPem, string PublicKeyBase64, string KeyId);

    public static KeyPair Generate()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo());
        return new KeyPair(ec.ExportPkcs8PrivateKeyPem(), pub, KeyIdOf(pub));
    }

    /// <summary>Short stable name for a public key: first 8 hex of sha256(SPKI).</summary>
    public static string KeyIdOf(string publicKeyBase64) => Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(publicKeyBase64)))[..8];

    public static void Sign(Pack pack, string privateKeyPem)
    {
        using var ec = ECDsa.Create();
        ec.ImportFromPem(privateKeyPem);
        PackJson.StampHashes(pack);
        var sig = ec.SignData(PackJson.EntriesBytes(pack), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        var pub = Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo());
        pack.Signature = new PackSignature { Alg = Alg, KeyId = KeyIdOf(pub), Value = Convert.ToBase64String(sig) };
    }

    public static bool Verify(Pack pack, string publicKeyBase64)
    {
        if (pack.Signature is null || pack.Signature.Alg != Alg) return false;
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return ec.VerifyData(PackJson.EntriesBytes(pack), Convert.FromBase64String(pack.Signature.Value), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return false; }
    }
}
