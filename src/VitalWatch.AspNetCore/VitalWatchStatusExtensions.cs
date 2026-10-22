using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VitalWatch.Internal;

namespace VitalWatch;

/// <summary>
/// Installation diagnostics: <c>dotnet run -- --vitalwatch-status</c>.
/// </summary>
/// <remarks>
/// <para>Exists for the package's most expensive failure mode: <b>silently not working.</b>
/// Nothing breaks when the configuration is incomplete or the secret is wrong — the hub
/// returns 204 even for an invalid request.</para>
/// <para><b>Why the application itself and not a separate tool.</b> The Node and Python
/// packages ship a <c>vitalwatch-status</c> command; in .NET the equivalent would be a separate
/// <c>dotnet tool</c>. Such a tool cannot see the configuration the application sees:
/// <c>appsettings.{Environment}.json</c>, user-secrets, Key Vault and settings passed in code via
/// <c>AddVitalWatch(o =&gt; ...)</c> only come together in the application's own host. A
/// diagnostic tool that gives the wrong diagnosis is worse than none, so the command runs on the
/// application's built host.</para>
/// <para><b>Why not an endpoint.</b> An HTTP endpoint such as <c>MapVitalWatchStatus()</c>
/// would expose the configuration (key, secret length, version) to the internet.</para>
/// </remarks>
public static class VitalWatchStatusExtensions
{
    /// <summary>The command-line flag.</summary>
    public const string Flag = "--vitalwatch-status";

    private const string NuGetIndex = "https://api.nuget.org/v3-flatcontainer/vitalwatch.aspnetcore/index.json";

    /// <summary>
    /// If <paramref name="args"/> contains <c>--vitalwatch-status</c>, prints the diagnostics,
    /// sets <see cref="Environment.ExitCode"/> and returns <c>true</c>; the caller should then exit
    /// without starting the application. Otherwise does nothing and returns <c>false</c>.
    /// </summary>
    /// <param name="host">The built application host.</param>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="output">Where to write the report; defaults to <see cref="Console.Out"/>.</param>
    /// <returns><c>true</c> if the diagnostics ran and the application should exit.</returns>
    /// <example>
    /// <code>
    /// var app = builder.Build();
    /// if (await app.RunVitalWatchStatusAsync(args)) return;
    /// </code>
    /// Extra flags: <c>--test</c> sends a real test exception; <c>--vitalwatch</c> sends only a
    /// liveness request, without creating an error in the panel.
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

            Line("SDK version", SdkInfo.Version);
            output.WriteLine();
            output.WriteLine(disabled
                ? "  ✗ VITALWATCH_ENABLED=false — no data is sent."
                : "  ✗ AddVitalWatch() was not called: builder.Services.AddVitalWatch();");
            output.WriteLine();

            return 1;
        }

        Line("SDK version", SdkInfo.Version);

        /*
         * Güncelleme denetimi yalnızca burada, izlenen uygulamanın çalışma yolunda değil.
         * Güncelleme olması uyarıdır, çıkış kodu değişmez; NuGet'e ulaşılamaması da.
         * VITALWATCH_STATUS_OFFLINE=1 denetimi atlar.
         */
        if (!IsTrue(Environment.GetEnvironmentVariable("VITALWATCH_STATUS_OFFLINE")))
        {
            var latest = await LatestVersionAsync().ConfigureAwait(false);
            Line("Latest version", latest ?? "could not check");

            if (latest is not null && IsNewer(latest, SdkInfo.Version))
            {
                output.WriteLine("  ! Update available: dotnet add package VitalWatch.AspNetCore");
            }
        }

        Line("Enabled", settings.Enabled ? "yes" : "NO (VITALWATCH_ENABLED=false)");
        Line("Hub URL", settings.Url ?? "not set");
        Line("Project key", settings.Key ?? "not set");
        Line("Secret length", settings.Secret is null ? "not set" : $"{settings.Secret.Length} characters");
        Line("Environment", settings.Env == settings.EnvRaw ? settings.Env : $"{settings.Env} ({settings.EnvRaw})");
        Line("Release tag", settings.Release is null
            ? "not set (source: none)"
            : $"{settings.Release} (source: {settings.ReleaseSource})");
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

        output.WriteLine("  ✓ Configuration OK.");

        var reporter = services.GetRequiredService<Reporter>();

        if (args.Contains("--test"))
        {
            // Gerçek bir hata gönderilir: hem taşıma hem temizlik sınanmış olur.
            var result = await reporter
                .SendNowAsync(new InvalidOperationException("Test event generated by vitalwatch-status --test"))
                .ConfigureAwait(false);

            /*
             * Sonuç okunuyor, "gönderdim" varsayılmıyor: Node paketinde ağ koptuğunda da
             * "✓ gönderildi" yazılıyordu ve kuran kişi kurulumu çalışır sanıyordu.
             */
            if (!result.Sent)
            {
                output.WriteLine($"  ✗ Test event could NOT be sent: {result.Error ?? $"HTTP {result.Status}"}");
                output.WriteLine();

                return 1;
            }

            output.WriteLine($"  ✓ Test event sent (HTTP {result.Status}).");
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
                output.WriteLine($"  ✗ Liveness request could NOT be sent: {result.Error ?? $"HTTP {result.Status}"}");
                output.WriteLine();

                return 1;
            }

            output.WriteLine($"  ✓ Liveness request sent (HTTP {result.Status}).");
        }

        output.WriteLine();
        output.WriteLine("  Only the hub panel can confirm that data arrives:");
        output.WriteLine("  the project row should show the connection status \"Connected\".");
        output.WriteLine("  The hub returns 204 even for an invalid signature; this command cannot tell.");
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
