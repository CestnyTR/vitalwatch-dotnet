using System.Text.RegularExpressions;

namespace VitalWatch.Internal;

/// <summary>
/// Sürüm etiketini (<c>release</c>) kendiliğinden bulur.
/// </summary>
/// <remarks>
/// Kural Laravel, Node ve Python paketleriyle aynı:
/// <list type="number">
/// <item>Açık değer (<c>VITALWATCH_RELEASE</c>, <c>VitalWatch:Release</c> ya da kod) —
/// kırpılır, en çok 64 karakter, olduğu gibi.</item>
/// <item>CI/PaaS değişkenleri (aşağıdaki sırayla ilk dolu olan). Sha biçimindeyse küçük harf
/// ilk 12 karakter, değilse kırpılmış en çok 64 karakter.</item>
/// <item>Uygulama kökündeki <c>.git</c> (HEAD → dal ref'i → packed-refs; ayrık HEAD). Yalnızca
/// geçerli 40 hanelik hex sha kabul edilir; ilk 12 karakter.</item>
/// <item>Hiçbiri yoksa etiket yok.</item>
/// </list>
///
/// .NET'e özgü fark: <c>.git</c> içerik kökünde yoksa en çok üç üst dizine bakılır. .NET
/// projeleri çoğunlukla çözüm klasörünün altında bir proje klasöründe çalışır
/// (<c>repo/src/Uygulama</c>); deploy <c>git pull</c> olduğunda <c>.git</c> repo kökündedir.
///
/// <c>git</c> programı çalıştırılmaz (alt süreç yok), yalnızca kurulumda birkaç küçük dosya
/// okunur. Hiçbir koşulda fırlatmaz.
/// </remarks>
internal static class ReleaseResolver
{
    public static readonly string[] EnvNames =
    [
        "GIT_COMMIT",
        "GIT_SHA",
        "COMMIT_SHA",
        "SOURCE_VERSION",
        "VERCEL_GIT_COMMIT_SHA",
        "RENDER_GIT_COMMIT",
        "HEROKU_SLUG_COMMIT",
        "CI_COMMIT_SHA",
    ];

    private const int MaxLength = 64;
    private const int ShortSha = 12;
    private const int ParentLevels = 3;

    private static readonly Regex ShaLike = new("^[0-9a-fA-F]{7,40}$", RegexOptions.CultureInvariant);
    private static readonly Regex FullSha = new("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant);
    private static readonly Regex SymbolicRef = new(@"^ref:\s*(\S+)", RegexOptions.CultureInvariant);
    private static readonly Regex GitDirLine = new(@"^gitdir:\s*(.+?)\s*$", RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <param name="explicitValue">Koddan, ortamdan ya da appsettings'ten gelen açık değer.</param>
    /// <param name="explicitSource">Açık değerin kaynağı (teşhis çıktısı için).</param>
    /// <param name="env">Ortam değişkeni okuyucu.</param>
    /// <param name="root">Uygulama kökü.</param>
    public static (string? Value, string? Source) Resolve(
        string? explicitValue,
        string? explicitSource,
        Func<string, string?> env,
        string? root)
    {
        try
        {
            var value = explicitValue?.Trim();
            if (!string.IsNullOrEmpty(value))
            {
                return (Scrubber.Truncate(value, MaxLength), explicitSource ?? "VITALWATCH_RELEASE");
            }

            foreach (var name in EnvNames)
            {
                var candidate = env(name)?.Trim();
                if (string.IsNullOrEmpty(candidate))
                {
                    continue;
                }

                return (
                    ShaLike.IsMatch(candidate)
                        ? candidate[..Math.Min(ShortSha, candidate.Length)].ToLowerInvariant()
                        : Scrubber.Truncate(candidate, MaxLength),
                    name);
            }

            if (!string.IsNullOrEmpty(root))
            {
                var directory = root;

                for (var level = 0; level <= ParentLevels && directory is not null; level++)
                {
                    if (Exists(System.IO.Path.Combine(directory, ".git")))
                    {
                        var sha = GitSha(directory);

                        return sha is null ? (null, null) : (sha, ".git");
                    }

                    directory = System.IO.Path.GetDirectoryName(directory);
                }
            }
        }
        catch
        {
            // Etiket bulunamadı — raporlama etiketsiz sürer.
        }

        return (null, null);
    }

    private static bool Exists(string path) => Directory.Exists(path) || File.Exists(path);

    /// <summary><c>.git</c>'ten 12 karakterlik sha; bulunamazsa null.</summary>
    internal static string? GitSha(string root)
    {
        try
        {
            var gitDir = ResolveGitDir(root);
            if (gitDir is null)
            {
                return null;
            }

            var head = Read(System.IO.Path.Combine(gitDir, "HEAD"));
            if (head is null)
            {
                return null;
            }

            var symbolic = SymbolicRef.Match(head);
            var sha = symbolic.Success ? ReadRef(gitDir, symbolic.Groups[1].Value) : head;

            return ValidSha(sha);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// <c>.git</c> dizinse kendisi. Dosyaysa (worktree, alt modül) içindeki
    /// <c>gitdir: &lt;yol&gt;</c> izlenir; göreli yol köke göre çözülür.
    /// </summary>
    private static string? ResolveGitDir(string root)
    {
        var dotGit = System.IO.Path.Combine(root, ".git");

        if (Directory.Exists(dotGit))
        {
            return dotGit;
        }

        if (!File.Exists(dotGit))
        {
            return null;
        }

        var match = GitDirLine.Match(Read(dotGit) ?? string.Empty);

        return match.Success
            ? System.IO.Path.GetFullPath(System.IO.Path.Combine(root, match.Groups[1].Value))
            : null;
    }

    /// <summary>Dal ref'i: önce gevşek dosya, yoksa packed-refs.</summary>
    private static string? ReadRef(string gitDir, string reference)
    {
        // `ref: ../../bir/dosya` gibi bir HEAD git dizininin dışına çıkamaz.
        if (!reference.StartsWith("refs/", StringComparison.Ordinal) || reference.Split('/').Contains(".."))
        {
            return null;
        }

        var loose = Read(System.IO.Path.Combine(gitDir, reference));
        if (loose is not null)
        {
            return loose;
        }

        var packed = Read(System.IO.Path.Combine(gitDir, "packed-refs"));
        if (packed is null)
        {
            return null;
        }

        foreach (var line in packed.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] is '#' or '^')
            {
                continue;
            }

            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[1] == reference)
            {
                return parts[0];
            }
        }

        return null;
    }

    private static string? ValidSha(string? value)
    {
        var sha = value?.Trim() ?? string.Empty;

        return FullSha.IsMatch(sha) ? sha[..ShortSha].ToLowerInvariant() : null;
    }

    private static string? Read(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
