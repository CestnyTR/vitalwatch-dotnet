using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace VitalWatch.Internal;

/// <summary>
/// İstek ölçümü: istisna, 5xx, yavaş istek ve istek başına sorgu sayacı.
/// </summary>
/// <remarks>
/// Davranış garantileri:
/// <list type="number">
/// <item><b>Hata yutulmaz.</b> İstisna kaydedilir ve <c>throw;</c> ile aynı stack'le yeniden
/// fırlatılır; uygulamanın hata işleyicisi çalışmaya devam eder.</item>
/// <item><b>İstek bekletilmez.</b> Burada yalnızca olay kurulup kuyruğa bırakılır; ağ işi arka
/// planda yapılır.</item>
/// <item>Ölçüm kodu kendi hatasını yutar; isteği asla bozmaz.</item>
/// </list>
/// </remarks>
internal sealed class VitalWatchMiddleware
{
    private readonly RequestDelegate _next;
    private readonly Reporter _reporter;

    public VitalWatchMiddleware(RequestDelegate next, Reporter reporter)
    {
        _next = next;
        _reporter = reporter;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        /*
         * Aynı istek ikinci kez geçiyor: hata sayfası ya da durum kodu sayfası isteği yeniden
         * yürütüyor. Ölçüm dıştaki geçişte yapılır; burada ölçülseydi "/Error" için ayrıca
         * bir "HTTP 500" açılırdı.
         */
        if (!_reporter.Active || context.Items.ContainsKey(RequestScope.ItemKey))
        {
            await _next(context);

            return;
        }

        RequestScope? scope = null;

        try
        {
            scope = new RequestScope(context, _reporter);
            context.Items[RequestScope.ItemKey] = scope;
            RequestScope.Current = scope;
        }
        catch
        {
            // Kapsam kurulamadı — istek ölçümsüz devam eder.
        }

        var started = Stopwatch.GetTimestamp();

        try
        {
            await _next(context);
        }
        catch (Exception exception)
        {
            OnException(context, exception);

            throw;
        }

        if (scope is not null)
        {
            Measure(context, scope, started);
        }
    }

    private void OnException(HttpContext context, Exception exception)
    {
        try
        {
            // İstemci bağlantıyı kesti: iptal arıza değil.
            if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
            {
                return;
            }

            _reporter.RecordException(exception, null, context);
        }
        catch
        {
            // Sessiz.
        }
    }

    private void Measure(HttpContext context, RequestScope scope, long started)
    {
        try
        {
            /*
             * İstemci kopmuş: süre kullanıcının kopmasıyla ölçülmüş olur, durum kodu da
             * anlamsız. Node paketindeki `finish`/`close` ayrımının karşılığı.
             */
            if (context.RequestAborted.IsCancellationRequested)
            {
                return;
            }

            var durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            _reporter.RecordRequest(scope, context.Response.StatusCode, durationMs);
        }
        catch
        {
            // Sessiz.
        }
    }
}
