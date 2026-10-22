using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VitalWatch.Internal;

/// <summary>
/// Çöken arka plan servislerini (<c>BackgroundService</c>) <c>job_failed</c> olarak raporlar.
/// </summary>
/// <remarks>
/// <para><b>Neden günlük sağlayıcısı.</b> <c>BackgroundService.ExecuteAsync</c> fırlattığında
/// .NET hiçbir olay ya da kanca yayınlamıyor; tek iz, host'un kendi günlüğüne yazdığı
/// "BackgroundService failed" kaydı (kategori <c>Microsoft.Extensions.Hosting.Internal.Host</c>,
/// seviye Error, istisnasıyla birlikte). Alternatifler daha kötüydü:</para>
/// <list type="bullet">
/// <item>Kullanıcının servislerini sarmalamak — <c>IHostedService</c> kayıtlarını değiştirmek
/// demek; sıra, tür ve fabrika kayıtları kırılgan.</item>
/// <item>Servisin <c>ExecuteTask</c>'ını yoklamak — zamanlayıcı, gecikme ve hangi servislerin
/// izleneceği sorusu.</item>
/// <item><c>AppDomain.UnhandledException</c> — BackgroundService hatası oraya hiç ulaşmıyor.</item>
/// </list>
///
/// <para>Yalnızca o kategori dinlenir; diğer bütün kategoriler için boş günlükçü döner ve
/// uygulamanın günlüğüne dokunulmaz. Host aynı istisnayı bir de "StopHost" kaydıyla (Critical)
/// yazıyor; istisna üstündeki işaret ikinciyi eler. İstek hatalarını ASP.NET Core ayrı
/// kategorilere yazar; onlar buraya düşmez, düşse de işaret yüzünden ikinci kez gitmez.</para>
/// </remarks>
[ProviderAlias("VitalWatch")]
internal sealed class VitalWatchLoggerProvider : ILoggerProvider
{
    public const string HostCategory = "Microsoft.Extensions.Hosting.Internal.Host";

    private readonly Reporter? _reporter;

    public VitalWatchLoggerProvider(Reporter? reporter)
    {
        _reporter = reporter;
    }

    public ILogger CreateLogger(string categoryName) =>
        _reporter is not null && categoryName == HostCategory
            ? new HostLogger(_reporter)
            : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public void Dispose()
    {
    }

    private sealed class HostLogger(Reporter reporter) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error && reporter.Active;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            try
            {
                if (exception is null || !IsEnabled(logLevel))
                {
                    return;
                }

                reporter.RecordJobFailed(exception, JobName(exception));
            }
            catch
            {
                // Kendi hatasını raporlamaz.
            }
        }
    }

    /// <summary>
    /// İstisnanın stack'inde geçen kullanıcı servisinin türü; bulunamazsa <c>host</c>.
    /// </summary>
    /// <remarks>
    /// Günlük kaydı servisin adını taşımıyor. Stack'te <c>ExecuteAsync</c>'ın durum makinesi
    /// (<c>&lt;ExecuteAsync&gt;d__3</c>) var; onun dış türü servisin kendisi.
    /// </remarks>
    internal static string JobName(Exception exception)
    {
        try
        {
            var frames = new System.Diagnostics.StackTrace(exception, fNeedFileInfo: false).GetFrames();

            foreach (var frame in frames)
            {
                var type = frame.GetMethod()?.DeclaringType;

                // Derleyicinin ürettiği iç tür (async durum makinesi, lambda kapanışı).
                while (type is not null && type.Name.StartsWith('<') && type.DeclaringType is not null)
                {
                    type = type.DeclaringType;
                }

                if (type is null || type.Namespace?.StartsWith("Microsoft.Extensions.Hosting", StringComparison.Ordinal) == true)
                {
                    continue;
                }

                if (typeof(IHostedService).IsAssignableFrom(type))
                {
                    return type.FullName ?? type.Name;
                }
            }
        }
        catch
        {
            // Ad bulunamadı.
        }

        return "host";
    }
}
