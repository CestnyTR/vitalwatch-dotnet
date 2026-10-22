using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VitalWatch.Internal;

namespace VitalWatch;

/// <summary>
/// Kurulum teşhisi: <c>dotnet run -- --vitalwatch-status</c>.
/// </summary>
/// <remarks>
/// <para>Paketin en pahalı arıza biçimi için var: <b>sessiz çalışmama.</b> Yapılandırma eksikken
/// ya da secret yanlışken hiçbir şey patlamaz — hub geçersiz isteğe de 204 döner.</para>
/// <para><b>Neden ayrı bir araç değil de uygulamanın kendisi.</b> Node ve Python paketleri
/// <c>vitalwatch-status</c> komutu taşıyor; .NET'te karşılığı ayrı bir <c>dotnet tool</c>
/// olurdu. O araç uygulamanın gördüğü yapılandırmayı göremez: <c>appsettings.{Ortam}.json</c>,
/// user-secrets, Key Vault, koddan verilen <c>AddVitalWatch(o =&gt; ...)</c> ayarları ancak
/// uygulamanın kendi host'unda birleşir. Node paketinde tam da bu yaşandı — <c>.env</c> doğru
/// doldurulmuşken teşhis komutu "TANIMSIZ" dedi. Teşhis aracının yanlış teşhis koyması, hiç
/// teşhis koymamaktan kötüdür. Bu yüzden komut, uygulamanın kurulmuş host'u üzerinde çalışır.</para>
/// <para><b>Neden uç nokta değil.</b> <c>MapVitalWatchStatus()</c> gibi bir HTTP ucu
/// yapılandırmayı (anahtar, secret uzunluğu, sürüm) internete açardı.</para>
/// </remarks>
public static class VitalWatchStatusExtensions
{
    /// <summary>Komut satırı bayrağı.</summary>
    public const string Flag = "--vitalwatch-status";

    private const string NuGetIndex = "https://api.nuget.org/v3-flatcontainer/vitalwatch.aspnetcore/index.json";

    /// <summary>
    /// <paramref name="args"/> <c>--vitalwatch-status</c> içeriyorsa teşhisi yazdırır, çıkış
    /// kodunu <see cref="Environment.ExitCode"/>'a koyar ve <c>true</c> döner; uygulama
    /// başlatılmadan çıkılmalıdır. İçermiyorsa hiçbir şey yapmaz ve <c>false</c> döner.
    /// </summary>
    /// <example>
    /// <code>
    /// var app = builder.Build();
    /// if (await app.RunVitalWatchStatusAsync(args)) return;
    /// </code>
    /// Ek bayraklar: <c>--test</c> gerçek bir sınama istisnası gönderir; <c>--vitalwatch</c>
    /// panele hata düşürmeden yalnızca canlılık isteği gönderir.
    /// </example>
    public static async Task<bool> RunVitalWatchStatusAsync(this IHost host, string[] args, TextWriter? output = null)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (args is null || !args.Contains(Flag))
        {
            return false;
        }

        Environment.ExitCode = await RunStatusAsync(host.Services, args, output ?? Console.Out).ConfigureAwait(false);

        return true;
    }

    internal static async Task<int> RunStatusAsync(IServiceProvider services, string[] args, TextWriter output)
    {
        void Line(string label, string value) => output.WriteLine($"  {label,-18} {value}");

        output.WriteLine();

        var settings = services.GetService<Settings>();

        if (settings is null)
        {
            var disabled = services.GetService<IVitalWatch>() is DisabledVitalWatch;

            Line("SDK sürümü", SdkInfo.Version);
            output.WriteLine();
            output.WriteLine(disabled
                ? "  ✗ VITALWATCH_ENABLED=false — hiçbir veri gönderilmez."
                : "  ✗ AddVitalWatch() çağrılmamış: builder.Services.AddVitalWatch();");
            output.WriteLine();

            return 1;
        }

        Line("SDK sürümü", SdkInfo.Version);

        /*
         * Güncelleme denetimi yalnızca burada, izlenen uygulamanın çalışma yolunda değil.
         * Güncelleme olması uyarıdır, çıkış kodu değişmez; NuGet'e ulaşılamaması da.
         * VITALWATCH_STATUS_OFFLINE=1 denetimi atlar.
         */
        if (!IsTrue(Environment.GetEnvironmentVariable("VITALWATCH_STATUS_OFFLINE")))
        {
            var latest = await LatestVersionAsync().ConfigureAwait(false);
            Line("Güncel sürüm", latest ?? "denetlenemedi");

            if (latest is not null && IsNewer(latest, SdkInfo.Version))
            {
                output.WriteLine("  ! Güncelleme var: dotnet add package VitalWatch.AspNetCore");
            }
        }

        Line("Etkin", settings.Enabled ? "evet" : "HAYIR (VITALWATCH_ENABLED=false)");
        Line("Hub adresi", settings.Url ?? "TANIMSIZ");
        Line("Proje anahtarı", settings.Key ?? "TANIMSIZ");
        Line("Secret uzunluğu", settings.Secret is null ? "TANIMSIZ" : $"{settings.Secret.Length} karakter");
        Line("Environment", settings.Env == settings.EnvRaw ? settings.Env : $"{settings.Env} ({settings.EnvRaw})");
        Line("Sürüm etiketi", settings.Release is null
            ? "tanımsız (kaynak: yok)"
            : $"{settings.Release} (kaynak: {settings.ReleaseSource})");
        Line(".NET", SdkInfo.RuntimeVersion);
        output.WriteLine();

        var problems = settings.Problems();

        if (problems.Count > 0)
        {
            foreach (var problem in problems)
            {
                output.WriteLine($"  ✗ {problem}");
            }

            output.WriteLine();

            return 1;
        }

        output.WriteLine("  ✓ Yapılandırma tamam.");

        var reporter = services.GetRequiredService<Reporter>();

        if (args.Contains("--test"))
        {
            // Gerçek bir hata gönderilir: hem taşıma hem temizlik sınanmış olur.
            var result = await reporter
                .SendNowAsync(new InvalidOperationException("vitalwatch-status --test ile üretilen sınama olayı"))
                .ConfigureAwait(false);

            /*
             * Sonuç okunuyor, "gönderdim" varsayılmıyor: Node paketinde ağ koptuğunda da
             * "✓ gönderildi" yazılıyordu ve kuran kişi kurulumu çalışır sanıyordu.
             */
            if (!result.Sent)
            {
                output.WriteLine($"  ✗ Sınama olayı GÖNDERİLEMEDİ: {result.Error ?? $"HTTP {result.Status}"}");
                output.WriteLine();

                return 1;
            }

            output.WriteLine($"  ✓ Sınama olayı gönderildi (HTTP {result.Status}).");
        }

        /*
         * --vitalwatch: bağlantıyı panele hata düşürmeden sınar. Onlarca kurulumu gezen bir
         * döngüde --test panele onlarca sahte hata bırakırdı.
         */
        if (args.Contains("--vitalwatch"))
        {
            var result = await reporter.HeartbeatAsync().ConfigureAwait(false);

            if (!result.Sent)
            {
                output.WriteLine($"  ✗ Canlılık isteği GÖNDERİLEMEDİ: {result.Error ?? $"HTTP {result.Status}"}");
                output.WriteLine();

                return 1;
            }

            output.WriteLine($"  ✓ Canlılık isteği gönderildi (HTTP {result.Status}).");
        }

        output.WriteLine();
        output.WriteLine("  Verinin ulaştığı yalnızca hub panelinden doğrulanır:");
        output.WriteLine("  proje satırında bağlantı durumu \"Bağlı\" görünmelidir.");
        output.WriteLine("  Hub geçersiz imzaya da 204 döner; buradan anlaşılmaz.");
        output.WriteLine();

        return 0;
    }

    private static bool IsTrue(string? value) =>
        value?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on" or "evet";

    /// <summary>NuGet'teki en güncel kararlı sürüm; alınamazsa <c>null</c>. Fırlatmaz.</summary>
    private static async Task<string?> LatestVersionAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            await using var stream = await http.GetStreamAsync(NuGetIndex).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);

            string? latest = null;

            foreach (var version in document.RootElement.GetProperty("versions").EnumerateArray())
            {
                var text = version.GetString();
                if (text is not null && !text.Contains('-') && (latest is null || IsNewer(text, latest)))
                {
                    latest = text;
                }
            }

            return latest;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="latest"/>, <paramref name="installed"/>'dan yeni mi. Sayısal karşılaştırma
    /// (1.10.0 &gt; 1.9.0). Aynı numarada ön sürüm her zaman eskidir. Geçersiz girdide <c>false</c>.
    /// </summary>
    internal static bool IsNewer(string latest, string installed)
    {
        static (Version? Number, bool Pre) Parse(string value)
        {
            var text = value.Trim().TrimStart('v');
            var plus = text.IndexOf('+');
            if (plus >= 0) text = text[..plus];
            var dash = text.IndexOf('-');
            var pre = dash >= 0;
            if (pre) text = text[..dash];

            return (Version.TryParse(text, out var v) ? v : null, pre);
        }

        var a = Parse(latest);
        var b = Parse(installed);

        if (a.Number is null || b.Number is null)
        {
            return false;
        }

        var cmp = a.Number.CompareTo(b.Number);

        return cmp != 0 ? cmp > 0 : !a.Pre && b.Pre;
    }
}
