using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace VitalWatch.Internal;

/// <summary>
/// Çözülmüş, değişmez yapılandırma. Bütün bileşenler bunu okur.
/// </summary>
internal sealed class Settings
{
    public const string SectionName = "VitalWatch";
    public const int SecretLength = 64;
    public const int TimeoutDefaultMs = 2000;
    public const int TimeoutMinMs = 100;
    public const int TimeoutMaxMs = 10000;

    public bool Enabled { get; init; } = true;
    public string? Url { get; init; }
    public string? Key { get; init; }
    public string? Secret { get; init; }

    /// <summary>Normalize edilmiş ortam adı.</summary>
    public string Env { get; init; } = "production";

    /// <summary>Normalleştirmeden önceki ham değer — teşhis çıktısı için.</summary>
    public string EnvRaw { get; init; } = "production";

    public string? Release { get; init; }
    public string? ReleaseSource { get; init; }
    public int TimeoutMs { get; init; } = TimeoutDefaultMs;
    public int SlowRequestMs { get; init; } = 1000;
    public int SlowQueryMs { get; init; } = 500;
    public IReadOnlyList<Type> Ignore { get; init; } = [];
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromHours(8);

    /// <summary>URL, anahtar ve secret dolu mu.</summary>
    public bool Configured =>
        !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(Key) && !string.IsNullOrWhiteSpace(Secret);

    /// <summary>Kanca kurulacak mı: etkin ve yapılandırılmış.</summary>
    public bool Active => Enabled && Configured;

    /// <summary>
    /// Yerelde görülebilen yanlış yapılandırmalar. Başlangıç uyarısı ve teşhis komutu
    /// aynı listeyi kullanır.
    /// </summary>
    /// <remarks>
    /// Bu ailenin tekrarlayan hatası <b>sessizce çalışmama</b>: hub geçersiz isteğe de 204
    /// döner, gönderen taraf reddedildiğini hiçbir zaman öğrenemez. En sık sebep eksik
    /// kopyalanmış secret.
    /// </remarks>
    public List<string> Problems()
    {
        var problems = new List<string>();

        if (!Enabled)
        {
            problems.Add("VITALWATCH_ENABLED=false — no data is sent.");

            return problems;
        }

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(Url)) missing.Add("VITALWATCH_URL");
        if (string.IsNullOrWhiteSpace(Key)) missing.Add("VITALWATCH_KEY");
        if (string.IsNullOrWhiteSpace(Secret)) missing.Add("VITALWATCH_SECRET");

        if (missing.Count > 0)
        {
            problems.Add($"{string.Join(", ", missing)} not set — monitoring is not active.");
        }

        if (!string.IsNullOrEmpty(Secret) && Secret.Length != SecretLength)
        {
            problems.Add(
                $"VITALWATCH_SECRET must be {SecretLength} characters, got {Secret.Length}. It may have been copied incompletely.");
        }

        // Yerel geri döngü adresi hattan çıkmaz; yerel hub'la geliştirme uyarı üretmesin.
        if (Url is not null && Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !IsLoopback(Url))
        {
            problems.Add("VITALWATCH_URL starts with http:// — the secret signature travels over an unencrypted connection.");
        }

        if (!EnvironmentNames.IsAccepted(Env))
        {
            problems.Add(
                $"Environment \"{EnvRaw}\" is not accepted by the hub — events and liveness requests are silently rejected. " +
                "Set VITALWATCH_ENV to production, staging or local.");
        }

        return problems;
    }

    /// <summary>
    /// Yapılandırmayı çözer.
    /// </summary>
    /// <param name="configuration">Uygulama yapılandırması; <c>VitalWatch</c> bölümü okunur.</param>
    /// <param name="env">Süreç ortamı okuyucu (test için değiştirilebilir).</param>
    /// <param name="hostEnvironmentName">ASP.NET Core ortam adı (<c>Development</c> …).</param>
    /// <param name="contentRoot">Uygulama kökü; <c>.git</c> burada aranır.</param>
    /// <param name="configure">Koddan verilen ayarlar — her şeyi yener.</param>
    public static Settings Resolve(
        IConfiguration? configuration,
        Func<string, string?> env,
        string? hostEnvironmentName,
        string? contentRoot,
        Action<VitalWatchOptions>? configure)
    {
        var options = new VitalWatchOptions();
        string? releaseSource = null;

        // 1. appsettings — en zayıf kaynak.
        var section = configuration?.GetSection(SectionName);
        if (section is not null)
        {
            Apply(options, name => section[name], "appsettings", ref releaseSource);
        }

        // 2. Süreç ortamı — dosyayı her zaman yener (kardeş paketlerdeki kural).
        Apply(options, name => env("VITALWATCH_" + ToEnvName(name)), "VITALWATCH_RELEASE", ref releaseSource);

        // 3. Kod — her şeyi yener (Node'daki `init({...})` gibi).
        var releaseBefore = options.Release;
        configure?.Invoke(options);
        if (!string.Equals(releaseBefore, options.Release, StringComparison.Ordinal))
        {
            releaseSource = "code";
        }

        var envRaw = string.IsNullOrWhiteSpace(options.Env)
            ? (string.IsNullOrWhiteSpace(hostEnvironmentName) ? "production" : hostEnvironmentName!.Trim())
            : options.Env!.Trim();

        var release = ReleaseResolver.Resolve(options.Release, releaseSource, env, contentRoot);

        return new Settings
        {
            Enabled = options.Enabled,
            Url = Clean(options.Url),
            Key = Clean(options.Key),
            Secret = Clean(options.Secret),
            Env = EnvironmentNames.Normalize(envRaw),
            EnvRaw = envRaw,
            Release = release.Value,
            ReleaseSource = release.Source,
            TimeoutMs = TimeoutMilliseconds(options.Timeout),
            SlowRequestMs = Math.Max(0, options.SlowRequestMs),
            SlowQueryMs = Math.Max(0, options.SlowQueryMs),
            Ignore = options.Ignore.Where(t => t is not null).ToArray(),
            HeartbeatInterval = options.HeartbeatInterval < TimeSpan.Zero ? TimeSpan.Zero : options.HeartbeatInterval,
        };
    }

    private static void Apply(VitalWatchOptions options, Func<string, string?> read, string releaseSourceName, ref string? releaseSource)
    {
        var enabled = Clean(read(nameof(VitalWatchOptions.Enabled)));
        if (enabled is not null)
        {
            options.Enabled = !IsFalse(enabled);
        }

        options.Url = Clean(read(nameof(VitalWatchOptions.Url))) ?? options.Url;
        options.Key = Clean(read(nameof(VitalWatchOptions.Key))) ?? options.Key;
        options.Secret = Clean(read(nameof(VitalWatchOptions.Secret))) ?? options.Secret;
        options.Env = Clean(read(nameof(VitalWatchOptions.Env))) ?? options.Env;

        var release = Clean(read(nameof(VitalWatchOptions.Release)));
        if (release is not null)
        {
            options.Release = release;
            releaseSource = releaseSourceName;
        }

        var timeout = Clean(read(nameof(VitalWatchOptions.Timeout)));
        if (timeout is not null)
        {
            // Geçersiz değer de "verildi" sayılır; kural onu varsayılana çevirir.
            options.Timeout = double.TryParse(timeout, NumberStyles.Float, CultureInfo.InvariantCulture, out var t)
                ? t
                : double.NaN;
        }

        if (int.TryParse(Clean(read(nameof(VitalWatchOptions.SlowRequestMs))), NumberStyles.Integer, CultureInfo.InvariantCulture, out var slowRequest))
        {
            options.SlowRequestMs = slowRequest;
        }

        if (int.TryParse(Clean(read(nameof(VitalWatchOptions.SlowQueryMs))), NumberStyles.Integer, CultureInfo.InvariantCulture, out var slowQuery))
        {
            options.SlowQueryMs = slowQuery;
        }
    }

    private static bool IsLoopback(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback;

    /// <summary><c>SlowRequestMs</c> → <c>SLOW_REQUEST_MS</c>.</summary>
    internal static string ToEnvName(string property)
    {
        var builder = new System.Text.StringBuilder(property.Length + 4);

        for (var i = 0; i < property.Length; i++)
        {
            var c = property[i];
            if (i > 0 && char.IsUpper(c))
            {
                builder.Append('_');
            }

            builder.Append(char.ToUpperInvariant(c));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Kapatma değerleri. Kardeş paketler yalnızca <c>false</c> tanıyor; burada
    /// <c>0</c>/<c>no</c>/<c>off</c> da kapatır — kapatma yönünde gevşek olmak güvenli taraf.
    /// </summary>
    internal static bool IsFalse(string value) =>
        value.Trim().ToLowerInvariant() is "false" or "0" or "no" or "off";

    /// <summary>
    /// <c>VITALWATCH_TIMEOUT</c> kuralı — bütün paketlerde aynı.
    /// </summary>
    /// <remarks>
    /// Node milisaniye, Laravel ve Python aynı adla saniye bekliyordu. <c>2</c> yazan birinin
    /// gönderimleri 2 ms'de kesilir, <c>2000</c> yazan birininki 2000 saniye beklerdi. Ortak
    /// kural: 100 ve üstü milisaniye, altı saniye. Geçersiz, sıfır, negatif ya da sonsuz değer
    /// varsayılana (2000 ms) döner. Sonuç [100, 10000] ms aralığına sıkıştırılır.
    /// </remarks>
    internal static int TimeoutMilliseconds(double? value)
    {
        if (value is not { } number || double.IsNaN(number) || double.IsInfinity(number) || number <= 0)
        {
            return TimeoutDefaultMs;
        }

        var ms = number < 100 ? number * 1000 : number;

        return (int)Math.Round(Math.Min(TimeoutMaxMs, Math.Max(TimeoutMinMs, ms)));
    }

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
