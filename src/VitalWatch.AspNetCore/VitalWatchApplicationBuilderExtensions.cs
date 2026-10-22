using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using VitalWatch.Internal;

namespace VitalWatch;

/// <summary>
/// <c>app.UseVitalWatch()</c>.
/// </summary>
public static class VitalWatchApplicationBuilderExtensions
{
    /// <summary>
    /// Adds request measurement: unhandled exceptions, 5xx responses, slow requests and the
    /// number of SQL queries per request.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The same <paramref name="app"/>, for chaining.</returns>
    /// <remarks>
    /// <para>Add it <b>as early as possible</b> — right after <c>builder.Build()</c>. Timing
    /// starts where the middleware is added; every middleware after it is included in the
    /// measurement.</para>
    /// <para>It does not matter whether it is added before or after <c>UseExceptionHandler</c>:
    /// if before, the exception caught by the handler is taken from the diagnostic event; if
    /// after, the middleware sees the exception itself and rethrows it.</para>
    /// <para>Nothing is added when monitoring is disabled or the configuration is incomplete.</para>
    /// </remarks>
    public static IApplicationBuilder UseVitalWatch(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Reporter? reporter = null;

        try
        {
            reporter = app.ApplicationServices.GetService<Reporter>();
        }
        catch
        {
            // Yapılandırma okunamadı — izleme kurulmaz, uygulama çalışır.
        }

        if (reporter is null || !reporter.Active)
        {
            return app;
        }

        return app.UseMiddleware<VitalWatchMiddleware>(reporter);
    }
}
