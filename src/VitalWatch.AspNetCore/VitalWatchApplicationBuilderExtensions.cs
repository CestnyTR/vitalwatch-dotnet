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
    /// İstek ölçümünü ekler: yakalanmamış istisna, 5xx yanıt, yavaş istek ve istek başına
    /// SQL sorgu sayısı.
    /// </summary>
    /// <remarks>
    /// <para><b>Mümkün olduğunca erken</b> ekleyin — <c>builder.Build()</c>'den hemen sonra.
    /// Süre ölçümü ara katmanın eklendiği noktadan başlar; sonraki her ara katman ölçüme dahil
    /// olur.</para>
    /// <para><c>UseExceptionHandler</c>'dan önce ya da sonra eklenmesi fark etmez: önceyse
    /// işleyicinin yakaladığı istisna tanılama olayından alınır, sonraysa ara katman
    /// istisnayı kendisi görür ve yeniden fırlatır.</para>
    /// <para>İzleme kapalıysa ya da yapılandırma eksikse hiçbir şey eklenmez.</para>
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
