using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VitalWatch.Internal;

/// <summary>
/// Başlangıç uyarısı, tanılama abonelikleri, canlılık nabzı ve kapanışta kuyruğun boşaltılması.
/// </summary>
internal sealed class VitalWatchHostedService : IHostedService
{
    private readonly Reporter _reporter;
    private readonly ILogger _logger;
    private CancellationTokenSource? _heartbeatStop;
    private Task? _heartbeat;

    public VitalWatchHostedService(Reporter reporter, ILoggerFactory loggerFactory)
    {
        _reporter = reporter;
        _logger = loggerFactory.CreateLogger("VitalWatch");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = _reporter.Settings;

            // VITALWATCH_ENABLED=false: hiçbir kanca, hiçbir uyarı.
            if (!settings.Enabled)
            {
                return Task.CompletedTask;
            }

            /*
             * TEK uyarı, bütün sorunlar bir arada. Bu ailenin tekrarlayan hatası sessizce
             * çalışmama: hub geçersiz isteğe de 204 döner, yanlış yapılandırmayı ancak burada
             * görebilirsiniz.
             */
            var problems = settings.Problems();
            if (problems.Count > 0)
            {
                _logger.LogWarning(
                    "VitalWatch yapılandırması eksik ya da hatalı: {Problems} Ayrıntı: dotnet run -- --vitalwatch-status",
                    string.Join(" ", problems));
            }

            if (!_reporter.Active)
            {
                return Task.CompletedTask;
            }

            DiagnosticsBridge.Register(_reporter);
            StartHeartbeat(settings.HeartbeatInterval);
        }
        catch
        {
            // İzleme kurulamadıysa uygulama yine başlar.
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Süreç boyunca düzenli "buradayım". Başlarken hemen bir istek atılır: deploy sonrası
    /// kurulum kendini anında kanıtlar, sekiz saat beklemez.
    /// </summary>
    /// <remarks>
    /// Hub 24 saat ses çıkmayan kurulumu "sessiz" sayıyor; 8 saat bunun üçte biri — iki tur
    /// kaçsa bile alarm çalmaz. Node ve Python paketleriyle aynı aralık ve aynı gövde
    /// (<c>{"events":[]}</c>).
    /// </remarks>
    private void StartHeartbeat(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero || _heartbeat is not null)
        {
            return;
        }

        _heartbeatStop = new CancellationTokenSource();
        var token = _heartbeatStop.Token;

        using (ExecutionContext.SuppressFlow())
        {
            _heartbeat = Task.Run(async () =>
            {
                try
                {
                    await _reporter.HeartbeatAsync(token).ConfigureAwait(false);

                    using var timer = new PeriodicTimer(interval);
                    while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                    {
                        await _reporter.HeartbeatAsync(token).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Kapanış ya da ağ — sessiz.
                }
            });
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _heartbeatStop?.Cancel();

            // Kuyruktakiler gitsin; süre kısa ve sınırlı — kapanışı uzatmaz.
            await _reporter.FlushAsync(TimeSpan.FromMilliseconds(Math.Min(_reporter.Settings.TimeoutMs + 500, 3000)))
                .ConfigureAwait(false);
        }
        catch
        {
            // Sessiz.
        }
    }
}
