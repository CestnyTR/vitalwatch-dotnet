using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VitalWatch.Internal;

/// <summary>Bir NuGet paketi: yalnızca ad ve sürüm.</summary>
internal readonly record struct DependencyPackage(string Name, string Version);

/// <summary>deps.json okumasının sonucu.</summary>
internal enum ManifestState
{
    Found,
    NotFound,
    Unreadable,
}

/// <summary>
/// Uygulamanın çözümlenmiş paket listesi (<c>{GirişDerlemesi}.deps.json</c>).
/// </summary>
/// <remarks>
/// <para>Yalnızca <c>libraries</c> içindeki <c>"type": "package"</c> girdileri alınır;
/// <c>project</c> (çözümdeki kendi projeleri) ve <c>reference</c> (doğrudan dll) atlanır —
/// OSV'de karşılıkları yok. Lisans, yol, sha512, hizmet bilgisi gönderilmez.</para>
/// <para>Hash dört SDK'da aynı kuralla: ada göre (ordinal, büyük/küçük harf duyarsız —
/// karşılaştırma küçük harfe çevrilmiş metin üzerinde, JS <c>toLowerCase</c> / Python
/// <c>lower</c> / PHP <c>strcasecmp</c> ile aynı sıra) sonra sürüme göre sıralı liste,
/// her satır <c>ad@sürüm\n</c>, UTF-8, sha256 hex küçük harf.</para>
/// </remarks>
internal sealed class DependencyManifest
{
    public const string Ecosystem = "NuGet";

    /// <summary>Gönderilecek en çok paket; fazlası sıralı listenin sonundan kesilir.</summary>
    public const int MaxPackages = 5000;

    /// <summary>Bundan büyük deps.json okunmaz — bellek ve süre sınırı.</summary>
    public const long MaxFileBytes = 32L * 1024 * 1024;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private DependencyManifest(ManifestState state, string fileName, IReadOnlyList<DependencyPackage> packages, string hash)
    {
        State = state;
        FileName = fileName;
        Packages = packages;
        Hash = hash;
    }

    public ManifestState State { get; }

    /// <summary>Örn. <c>MyApp.deps.json</c> — yol değil, yalnızca dosya adı.</summary>
    public string FileName { get; }

    public IReadOnlyList<DependencyPackage> Packages { get; }

    public string Hash { get; }

    public bool Found => State == ManifestState.Found;

    /// <summary>
    /// Giriş derlemesinin yanındaki deps.json'ın yolu; giriş derlemesi yoksa <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Tek dosya yayında (single-file) deps.json pakete gömülü; diskte yok — durum komutu
    /// "deps.json not found" der, hiçbir şey gönderilmez.
    /// </remarks>
    public static string? DefaultPath()
    {
        try
        {
            var name = Assembly.GetEntryAssembly()?.GetName().Name;

            return string.IsNullOrEmpty(name) ? null : Path.Combine(AppContext.BaseDirectory, name + ".deps.json");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Dosyayı okur. Fırlatmaz.</summary>
    public static DependencyManifest Load(string? path)
    {
        var fileName = string.IsNullOrEmpty(path) ? "deps.json" : Path.GetFileName(path);

        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return Empty(ManifestState.NotFound, fileName);
            }

            if (new FileInfo(path).Length > MaxFileBytes)
            {
                return Empty(ManifestState.Unreadable, fileName);
            }

            return Parse(File.ReadAllBytes(path), fileName);
        }
        catch
        {
            return Empty(ManifestState.Unreadable, fileName);
        }
    }

    /// <summary>deps.json içeriğini ayrıştırır. Fırlatmaz.</summary>
    public static DependencyManifest Parse(ReadOnlyMemory<byte> json, string fileName)
    {
        try
        {
            using var document = JsonDocument.Parse(json, DocumentOptions);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("libraries", out var libraries)
                || libraries.ValueKind != JsonValueKind.Object)
            {
                return Empty(ManifestState.Unreadable, fileName);
            }

            var seen = new HashSet<(string, string)>(new LowerPairComparer());
            var packages = new List<DependencyPackage>();

            foreach (var library in libraries.EnumerateObject())
            {
                if (library.Value.ValueKind != JsonValueKind.Object
                    || !library.Value.TryGetProperty("type", out var type)
                    || type.ValueKind != JsonValueKind.String
                    || !string.Equals(type.GetString(), "package", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Anahtar "Ad/Sürüm"; NuGet adında '/' olamaz.
                var key = library.Name;
                var slash = key.IndexOf('/');
                if (slash <= 0 || slash == key.Length - 1)
                {
                    continue;
                }

                var name = key[..slash].Trim();
                var version = key[(slash + 1)..].Trim();

                if (name.Length == 0 || version.Length == 0 || !seen.Add((name, version)))
                {
                    continue;
                }

                packages.Add(new DependencyPackage(name, version));
            }

            var canonical = Canonical(packages);

            return new DependencyManifest(ManifestState.Found, fileName, canonical, ComputeHash(canonical));
        }
        catch
        {
            return Empty(ManifestState.Unreadable, fileName);
        }
    }

    /// <summary>Sıralı ve <see cref="MaxPackages"/> ile kesilmiş liste.</summary>
    internal static IReadOnlyList<DependencyPackage> Canonical(IEnumerable<DependencyPackage> packages)
    {
        var sorted = packages.ToList();
        sorted.Sort(Compare);

        if (sorted.Count > MaxPackages)
        {
            sorted.RemoveRange(MaxPackages, sorted.Count - MaxPackages);
        }

        return sorted;
    }

    /// <summary>Kanonik listenin hash'i: her satır <c>ad@sürüm\n</c>, sha256 hex.</summary>
    internal static string ComputeHash(IReadOnlyList<DependencyPackage> canonical)
    {
        var builder = new StringBuilder(canonical.Count * 32);

        foreach (var package in canonical)
        {
            builder.Append(package.Name).Append('@').Append(package.Version).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    /// <summary>
    /// Ada, sonra sürüme göre; ikisi de küçük harfe çevrilmiş ordinal. Eşitlikte ham ordinal —
    /// sıra her çalıştırmada aynı olsun.
    /// </summary>
    /// <remarks>
    /// .NET'in <c>OrdinalIgnoreCase</c>'i büyük harfe çevirerek karşılaştırır; <c>_</c> harflerin
    /// büyük ve küçük biçimleri arasında durduğu için diğer dillerle farklı sıralardı.
    /// </remarks>
    internal static int Compare(DependencyPackage a, DependencyPackage b)
    {
        var byName = string.CompareOrdinal(a.Name.ToLowerInvariant(), b.Name.ToLowerInvariant());
        if (byName != 0)
        {
            return byName;
        }

        var byVersion = string.CompareOrdinal(a.Version.ToLowerInvariant(), b.Version.ToLowerInvariant());
        if (byVersion != 0)
        {
            return byVersion;
        }

        var raw = string.CompareOrdinal(a.Name, b.Name);

        return raw != 0 ? raw : string.CompareOrdinal(a.Version, b.Version);
    }

    private static DependencyManifest Empty(ManifestState state, string fileName) =>
        new(state, fileName, [], string.Empty);

    /// <summary>Tekilleştirme: ad ve sürüm büyük/küçük harf duyarsız.</summary>
    private sealed class LowerPairComparer : IEqualityComparer<(string, string)>
    {
        public bool Equals((string, string) x, (string, string) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Item1, y.Item1)
            && StringComparer.OrdinalIgnoreCase.Equals(x.Item2, y.Item2);

        public int GetHashCode((string, string) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item1),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2));
    }
}
