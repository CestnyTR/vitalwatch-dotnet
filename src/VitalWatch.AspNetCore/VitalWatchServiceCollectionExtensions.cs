using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VitalWatch.Internal;

namespace VitalWatch;

/// <summary>
/// <c>builder.Services.AddVitalWatch()</c>.
/// </summary>
public static class VitalWatchServiceCollectionExtensions
{
    /// <summary>
    /// Registers VitalWatch. Configuration is read from the <c>VitalWatch</c> section and the
    /// <c>VITALWATCH_*</c> environment variables; <paramref name="configure"/> overrides both.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional settings in code; they override every other source.</param>
    /// <returns>The same <paramref name="services"/>, for chaining.</returns>
    /// <remarks>
    /// <para>With <c>VITALWATCH_ENABLED=false</c> (or <c>VitalWatch:Enabled=false</c>)
    /// <b>nothing is installed</b>: no middleware, no logger provider, no diagnostic
    /// subscription, no background task. Only an empty <see cref="IVitalWatch"/> is registered so
    /// that code injecting it keeps working. This is how to turn monitoring off without
    /// redeploying when you suspect a problem.</para>
    /// <para>For request measurement, <c>app.UseVitalWatch()</c> must also be called.</para>
    /// </remarks>
    public static IServiceCollection AddVitalWatch(this IServiceCollection services, Action<VitalWatchOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(d => d.ServiceType == typeof(VitalWatchMarker)))
        {
            return services;
        }

        services.AddSingleton<VitalWatchMarker>();

        /*
         * Kayıt anında karar: WebApplicationBuilder yapılandırmayı ve ortamı hizmet
         * koleksiyonuna hazır örnek olarak koyuyor, appsettings bu noktada okunmuş durumda.
         * Kapalıysa hiçbir şey kaydedilmez. Bulunamazsa (alışılmadık host) karar çalışma
         * anına kalır — bileşenler kapalı ayarla zaten hiçbir şey yapmaz.
         */
        var early = TryResolveEarly(services, configure);
        if (early is { Enabled: false })
        {
            services.TryAddSingleton<IVitalWatch>(DisabledVitalWatch.Instance);

            return services;
        }

        services.TryAddSingleton(sp => Settings.Resolve(
            sp.GetService<IConfiguration>(),
            Environment.GetEnvironmentVariable,
            sp.GetService<IHostEnvironment>()?.EnvironmentName,
            sp.GetService<IHostEnvironment>()?.ContentRootPath ?? Directory.GetCurrentDirectory(),
            configure));

        services.TryAddSingleton(sp => new Reporter(sp.GetRequiredService<Settings>()));
        services.TryAddSingleton<IVitalWatch>(sp => sp.GetRequiredService<Reporter>());
        services.AddHostedService<VitalWatchHostedService>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, VitalWatchLoggerProvider>(sp =>
        {
            var reporter = sp.GetRequiredService<Reporter>();

            return new VitalWatchLoggerProvider(reporter.Active ? reporter : null);
        }));

        /*
         * Sağlayıcıya özel filtre: uygulama "Microsoft": "None" gibi geniş bir kural koysa da
         * host'un Error kaydı bize ulaşsın; diğer her şey sağlayıcıya hiç gelmesin.
         */
        services.Configure<LoggerFilterOptions>(options =>
        {
            options.AddFilter<VitalWatchLoggerProvider>(null, LogLevel.None);
            options.AddFilter<VitalWatchLoggerProvider>(VitalWatchLoggerProvider.HostCategory, LogLevel.Error);
        });

        return services;
    }

    private static Settings? TryResolveEarly(IServiceCollection services, Action<VitalWatchOptions>? configure)
    {
        try
        {
            var configuration = Registered<IConfiguration>(services);
            var host = Registered<IHostEnvironment>(services);

            // Sürüm etiketi burada gerekmiyor; .git okunmasın diye kök verilmiyor.
            return Settings.Resolve(configuration, Environment.GetEnvironmentVariable, host?.EnvironmentName, null, configure);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Koleksiyondaki hazır örnek. Host yapılandırmayı <c>_ =&gt; configuration</c> biçiminde
    /// fabrikayla da kaydediyor; fabrika boş bir sağlayıcıyla çağrılır — başka hizmet isteyen
    /// fabrika fırlatır ve karar çalışma anına kalır.
    /// </summary>
    private static T? Registered<T>(IServiceCollection services)
        where T : class
    {
        var descriptor = services.LastOrDefault(d => d.ServiceType == typeof(T) && !d.IsKeyedService);

        if (descriptor?.ImplementationInstance is T instance)
        {
            return instance;
        }

        if (descriptor?.ImplementationFactory is { } factory)
        {
            using var empty = new ServiceCollection().BuildServiceProvider();

            return factory(empty) as T;
        }

        return null;
    }

    /// <summary>İkinci <c>AddVitalWatch</c> çağrısını etkisiz kılar.</summary>
    internal sealed class VitalWatchMarker;
}

/// <summary>Kapalı modda kaydedilen boş uygulama.</summary>
internal sealed class DisabledVitalWatch : IVitalWatch
{
    public static readonly DisabledVitalWatch Instance = new();

    public bool Enabled => false;

    public bool Report(Exception exception, string? kind = null) => false;

    public Task<ReportResult> HeartbeatAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ReportResult.Failed("disabled"));

    public Task<bool> FlushAsync(TimeSpan timeout) => Task.FromResult(true);
}
