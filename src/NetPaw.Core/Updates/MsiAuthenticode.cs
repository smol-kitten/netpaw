using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace NetPaw.Updates;

public sealed record SignatureCheck(bool Ok, string Reason, string? Signer = null, DateTimeOffset? Timestamp = null);

/// <summary>
/// Authenticode verification of an MSI against PINNED roots only (never the machine store), in managed code so
/// it runs and is tested the same on every OS. A file passes when all of these hold:
///   1. its content digest (the MSI streams in Authenticode order, see <see cref="ContentDigest"/>) equals the
///      digest the signature covers, and the CMS signature over it verifies;
///   2. it carries an RFC 3161 timestamp countersignature whose token covers this signature and whose TSA
///      chains to a pinned root;
///   3. the signer chains to a pinned root (custom root trust), valid AT THE TIMESTAMP TIME (run leaves live an
///      hour; the timestamp is what keeps them valid), with the code-signing EKU.
/// </summary>
public static class MsiAuthenticode
{
    const string SpcIndirectData = "1.3.6.1.4.1.311.2.1.4";
    const string Rfc3161Countersignature = "1.3.6.1.4.1.311.3.3.1";
    const string CodeSigning = "1.3.6.1.5.5.7.3.3", TimeStamping = "1.3.6.1.5.5.7.3.8";
    static readonly byte[] DigitalSignature = Name("\u0005DigitalSignature"), DigitalSignatureEx = Name("\u0005MsiDigitalSignatureEx");

    /// <param name="intermediates">Extra certificates for chain BUILDING only (never trusted by themselves): the public
    /// catboy CA/TSA intermediates, in case a timestamp token carries just its own certificate.</param>
    public static SignatureCheck Verify(byte[] msi, IReadOnlyCollection<X509Certificate2> pinnedRoots, IReadOnlyCollection<X509Certificate2>? intermediates = null)
    {
        if (pinnedRoots.Count == 0) return new(false, "no pinned root");
        CompoundFile cf;
        try { cf = new CompoundFile(msi); }
        catch (InvalidDataException ex) { return new(false, "not a valid MSI: " + ex.Message); }

        var top = cf.ChildrenOf(cf.Root).ToList();
        if (top.Any(e => e.RawName.AsSpan().SequenceEqual(DigitalSignatureEx)))
            return new(false, "MsiDigitalSignatureEx signatures are not supported");
        var sigEntry = top.FirstOrDefault(e => e.IsStream && e.RawName.AsSpan().SequenceEqual(DigitalSignature));
        if (sigEntry is null) return new(false, "not signed");

        var cms = new SignedCms();
        try { cms.Decode(cf.Read(sigEntry, (uint)sigEntry.Size)); }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException) { return new(false, "unreadable signature: " + ex.Message); }
        if (cms.ContentInfo.ContentType.Value != SpcIndirectData) return new(false, "not an Authenticode signature");
        if (cms.SignerInfos.Count != 1) return new(false, "expected exactly one signer");
        var signer = cms.SignerInfos[0];

        // 1. content digest + CMS signature
        HashAlgorithmName alg; byte[] signedDigest;
        try { (alg, signedDigest) = ReadIndirectDigest(cms.ContentInfo.Content); }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException) { return new(false, "unreadable signed digest: " + ex.Message); }
        byte[] actual;
        try { actual = ContentDigest(cf, alg); }
        catch (InvalidDataException ex) { return new(false, "not a valid MSI: " + ex.Message); }
        if (!CryptographicOperations.FixedTimeEquals(actual, signedDigest)) return new(false, "file was modified after signing (digest mismatch)");
        try { signer.CheckSignature(verifySignatureOnly: true); }
        catch (CryptographicException ex) { return new(false, "signature does not verify: " + ex.Message); }
        var signerCert = signer.Certificate;
        if (signerCert is null) return new(false, "signer certificate missing");

        // 2. timestamp
        var tsAttr = signer.UnsignedAttributes.Cast<CryptographicAttributeObject>().FirstOrDefault(a => a.Oid.Value == Rfc3161Countersignature);
        if (tsAttr is null || tsAttr.Values.Count == 0) return new(false, "no timestamp");
        if (!Rfc3161TimestampToken.TryDecode(tsAttr.Values[0].RawData, out var token, out _)) return new(false, "unreadable timestamp");
        if (!token.VerifySignatureForSignerInfo(signer, out var tsaCert, cms.Certificates) || tsaCert is null) return new(false, "timestamp does not cover this signature");
        var at = token.TokenInfo.Timestamp;
        var extra = new X509Certificate2Collection(); extra.AddRange(cms.Certificates); extra.AddRange(token.AsSignedCms().Certificates);
        foreach (var c in intermediates ?? []) extra.Add(c);
        var tsaChain = Chain(tsaCert, extra, pinnedRoots, at, TimeStamping);
        if (tsaChain is not null) return new(false, "timestamp authority not trusted: " + tsaChain);

        // 3. signer chain at the timestamp time
        var chainError = Chain(signerCert, extra, pinnedRoots, at, CodeSigning);
        if (chainError is not null) return new(false, "signer not trusted: " + chainError);
        return new(true, "signed and verified", signerCert.GetNameInfo(X509NameType.SimpleName, false), at);
    }

    /// <summary>Authenticode MSI digest (as osslsigncode and Windows compute it): per storage, children sorted by raw
    /// UTF-16 name bytes, streams hashed in that order (signature streams at the root skipped), sub-storages recursed,
    /// then the storage's CLSID.</summary>
    public static byte[] ContentDigest(CompoundFile cf, HashAlgorithmName alg)
    {
        using var h = IncrementalHash.CreateHash(alg);
        HashStorage(cf, cf.Root, h, isRoot: true);
        return h.GetHashAndReset();
    }

    static void HashStorage(CompoundFile cf, CompoundFile.Entry dir, IncrementalHash h, bool isRoot)
    {
        var kids = cf.ChildrenOf(dir).ToList();
        kids.Sort((a, b) => CompareHashOrder(a.RawName, b.RawName));
        foreach (var k in kids)
        {
            if (isRoot && (k.RawName.AsSpan().SequenceEqual(DigitalSignature) || k.RawName.AsSpan().SequenceEqual(DigitalSignatureEx))) continue;
            if (k.IsStream)
            {
                var len = (uint)k.Size;
                if (len == 0 || len >= 0xFFFFFFFA) continue;   // osslsigncode skips empty and corrupt-sized streams
                h.AppendData(cf.Read(k, len));
            }
            else if (k.IsStorage) HashStorage(cf, k, h, false);
        }
        h.AppendData(dir.Clsid);
    }

    // memcmp over the shorter raw name (terminator included); on a tie the longer name sorts first
    static int CompareHashOrder(byte[] a, byte[] b)
    {
        var d = a.AsSpan(0, Math.Min(a.Length, b.Length)).SequenceCompareTo(b.AsSpan(0, Math.Min(a.Length, b.Length)));
        return d != 0 ? d : a.Length == b.Length ? 0 : a.Length > b.Length ? -1 : 1;
    }

    static (HashAlgorithmName, byte[]) ReadIndirectDigest(byte[] content)
    {
        // SpcIndirectDataContent ::= SEQUENCE { data SpcAttributeTypeAndOptionalValue, messageDigest DigestInfo }
        var r = new AsnReader(content, AsnEncodingRules.DER).ReadSequence();
        r.ReadEncodedValue();
        var di = r.ReadSequence();
        var algId = di.ReadSequence(); var oid = algId.ReadObjectIdentifier();
        var digest = di.ReadOctetString();
        var alg = oid switch
        {
            "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
            "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
            "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
            _ => throw new CryptographicException($"digest algorithm {oid} is not accepted"),
        };
        return (alg, digest);
    }

    static string? Chain(X509Certificate2 cert, X509Certificate2Collection extra, IReadOnlyCollection<X509Certificate2> roots, DateTimeOffset at, string eku)
    {
        using var chain = new X509Chain();
        var p = chain.ChainPolicy;
        p.TrustMode = X509ChainTrustMode.CustomRootTrust;
        foreach (var r in roots) p.CustomTrustStore.Add(r);
        p.ExtraStore.AddRange(extra);
        p.RevocationMode = X509RevocationMode.NoCheck;   // offline by design (no CRL fetch from the tray); run leaves live one hour
        p.VerificationTime = at.UtcDateTime;
        p.ApplicationPolicy.Add(new Oid(eku));
        if (chain.Build(cert)) return null;
        return string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()).Where(s => s.Length > 0).DefaultIfEmpty("chain did not build"));
    }

    static byte[] Name(string s) => [.. System.Text.Encoding.Unicode.GetBytes(s), 0, 0];
}
