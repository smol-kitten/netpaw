using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NetPaw.Updates;

/// <summary>
/// What a downloaded update must prove before the tray offers to install it: the release's SIGNATURES.md lists
/// its exact sha256, and its Authenticode signature chains to a root pinned in THIS build (never the machine
/// store). Staging R0 today; the second slot is for the production root once it exists (docs/signed-release.md).
/// </summary>
public static class UpdateTrust
{
    public const string R0Sha256 = "ed5eaa1b7a66e154e8c82a3d45f65ddd36a3fbbad1f41a68d2bf4334ccc2d375";   // staging R0, = deploy/pki/r0.crt
    public const string? R1Sha256 = null;                                                                    // production root: not issued yet
    public const long MaxDownload = 64L * 1024 * 1024;

    static readonly Lazy<IReadOnlyList<X509Certificate2>> Roots = new(() =>
    {
        var list = new List<X509Certificate2>();
        foreach (var (res, sha) in new[] { ("r0.crt", R0Sha256), ("r1.crt", R1Sha256) })
        {
            if (sha is null) continue;
            using var s = typeof(UpdateTrust).Assembly.GetManifestResourceStream(res) ?? throw new InvalidOperationException($"pinned root {res} not embedded");
            using var ms = new MemoryStream(); s.CopyTo(ms);
            var cert = X509CertificateLoader.LoadCertificate(ms.ToArray());
            if (!Convert.ToHexStringLower(SHA256.HashData(cert.RawData)).Equals(sha)) throw new InvalidOperationException($"embedded {res} does not match its pin");
            list.Add(cert);
        }
        return list;
    });

    /// <summary>The roots an update may chain to (each checked against its fingerprint pin at load).</summary>
    public static IReadOnlyList<X509Certificate2> PinnedRoots => Roots.Value;

    static readonly Lazy<IReadOnlyList<X509Certificate2>> Inter = new(() => new[] { "cb0.crt", "t0.crt" }.Select(res =>
    {
        using var s = typeof(UpdateTrust).Assembly.GetManifestResourceStream(res) ?? throw new InvalidOperationException($"{res} not embedded");
        using var ms = new MemoryStream(); s.CopyTo(ms);
        return X509CertificateLoader.LoadCertificate(ms.ToArray());
    }).ToList());

    /// <summary>Public catboy intermediates (CB0, timestamp CA T0) for chain building only; trust still ends at <see cref="PinnedRoots"/>.</summary>
    public static IReadOnlyList<X509Certificate2> Intermediates => Inter.Value;

    /// <summary>File name → sha256 (lowercase hex) from a catboy-sign SIGNATURES.md table (<c>| `path` | sha256 | kind |</c>).</summary>
    public static Dictionary<string, string> ParseSignatures(string markdown)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in markdown.Split('\n'))
        {
            var cells = line.Split('|');
            if (cells.Length < 4) continue;
            var path = cells[1].Trim().Trim('`'); var hash = cells[2].Trim().ToLowerInvariant();
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit) || path.Length == 0) continue;
            map.TryAdd(Path.GetFileName(path.Replace('\\', '/')), hash);
        }
        return map;
    }

    /// <summary>Both checks, sha256 first (cheap, and catches a swapped asset before any parsing).</summary>
    public static SignatureCheck Verify(byte[] file, string fileName, string signaturesMarkdown, IReadOnlyCollection<X509Certificate2> roots)
    {
        if (!ParseSignatures(signaturesMarkdown).TryGetValue(fileName, out var listed)) return new(false, $"{fileName} is not listed in SIGNATURES.md");
        var actual = Convert.ToHexStringLower(SHA256.HashData(file));
        if (actual != listed) return new(false, $"sha256 {actual[..12]}… does not match SIGNATURES.md ({listed[..12]}…)");
        return MsiAuthenticode.Verify(file, roots, Intermediates);
    }
}
