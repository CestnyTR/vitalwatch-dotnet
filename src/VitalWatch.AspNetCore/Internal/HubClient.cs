using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace VitalWatch.Internal;

/// <summary>
/// Hub'a HMAC imzalı POST.
/// </summary>
/// <remarks>
/// Davranış garantisi: <b>hiçbir koşulda hata fırlatmaz.</b> Hub erişilemezse, yapılandırma
/// eksikse veya ağ koparsa sessizce vazgeçilir — izlenen uygulamada hata, log kirliliği veya
/// yavaşlama oluşmaz.
/// </remarks>
internal sealed class HubClient : IDisposable
{
    private readonly Settings _settings;
    private readonly HttpClient _http;
    private readonly Uri? _endpoint;
    private readonly Uri? _depsEndpoint;

    public HubClient(Settings settings)
    {
        _settings = settings;

        var handler = new SocketsHttpHandler
        {
            // Yönlendirme takip edilmez: imzalı gövdeyi bilinmeyen bir adrese göndermek istemeyiz.
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromMilliseconds(settings.TimeoutMs),

            /*
             * İzleme isteği uygulamanın dağıtık izine karışmasın: traceparent başlığı hub'a
             * gitmez. (Activity yine de oluşabilir; başlık yayılmaz.)
             */
            ActivityHeadersPropagator = DistributedContextPropagator.CreateNoOutputPropagator(),
        };

        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMilliseconds(settings.TimeoutMs),
        };

        if (settings.Configured)
        {
            var root = $"{settings.Url!.TrimEnd('/')}/api/i/{Uri.EscapeDataString(settings.Key!)}/";

            Uri.TryCreate(root + "server", UriKind.Absolute, out _endpoint);

            // Bağımlılık listesi ayrı uca gider ama imza, başlıklar ve bağlantı havuzu aynı.
            Uri.TryCreate(root + "deps", UriKind.Absolute, out _depsEndpoint);
        }
    }

    /// <summary>
    /// Sonuç döndürür ama <b>asla hata fırlatmaz.</b>
    /// </summary>
    /// <remarks>
    /// Sonucu yalnızca teşhis komutu okuyor; izleme yolu görmezden geliyor. Hub başarıda da
    /// geçersiz istekte de 204 döner — yani 204 "kabul edildi" DEĞİL, yalnızca "istek ulaştı"
    /// demek.
    /// </remarks>
    public Task<ReportResult> SendAsync(byte[] body, CancellationToken cancellationToken = default) =>
        PostAsync(_endpoint, body, cancellationToken);

    /// <summary>
    /// Bağımlılık listesi: <c>/api/i/{key}/deps</c>. İmza <see cref="SendAsync"/> ile birebir
    /// aynı (aynı kod yolu). Fırlatmaz.
    /// </summary>
    public Task<ReportResult> SendDependenciesAsync(byte[] body, CancellationToken cancellationToken = default) =>
        PostAsync(_depsEndpoint, body, cancellationToken);

    private async Task<ReportResult> PostAsync(Uri? endpoint, byte[] body, CancellationToken cancellationToken)
    {
        if (endpoint is null || !_settings.Configured)
        {
            return ReportResult.Failed("missing-config");
        }

        try
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new ByteArrayContent(body),
            };

            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            // Zaman damgası imzaya dahildir; olmasaydı saldırgan damgayı değiştirip eski bir
            // gövdeyi yeniden oynatabilirdi.
            request.Headers.TryAddWithoutValidation("X-VitalWatch-Signature", "sha256=" + Sign(_settings.Secret!, timestamp, body));
            request.Headers.TryAddWithoutValidation("X-VitalWatch-Timestamp", timestamp);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_settings.TimeoutMs);

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            var status = (int)response.StatusCode;

            return new ReportResult(status < 400, status, status < 400 ? null : "HTTP " + status);
        }
        catch (Exception e)
        {
            // Sessizce vazgeç. İzleme paketinin izlediği uygulamayı bozması, çözdüğü
            // sorundan büyük bir sorundur.
            return ReportResult.Failed(e is OperationCanceledException ? "timeout" : e.GetType().Name + ": " + e.Message);
        }
    }

    /// <summary><c>hex(hmac_sha256(secret, "{timestamp}." + gövde))</c>, küçük harf.</summary>
    internal static string Sign(string secret, string timestamp, byte[] body)
    {
        var prefix = Encoding.UTF8.GetBytes(timestamp + ".");
        var payload = new byte[prefix.Length + body.Length];
        Buffer.BlockCopy(prefix, 0, payload, 0, prefix.Length);
        Buffer.BlockCopy(body, 0, payload, prefix.Length, body.Length);

        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void Dispose() => _http.Dispose();
}
