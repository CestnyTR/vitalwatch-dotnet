using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace VitalWatch.Tests;

/// <summary>
/// Testler için sahte hub: hub'ın imza doğrulamasını birebir uygular.
/// </summary>
/// <remarks>
/// Gerçek hub geçersiz isteğe de 204 döner. Burada da 204 dönülür ama istek
/// <b>kaydedilir</b>: testin görmesi gereken şey yanıt değil, hub'ın gövdeyi kabul edip
/// etmeyeceği. "204 aldım" yeterli sayılmaz. Kurallar vitalwatch-hub
/// <c>VerifyProjectSignature</c> ve <c>IngestController</c>'dan.
/// </remarks>
public sealed class FakeHub : IAsyncDisposable
{
    public const int MaxBodyBytes = 8192;
    public const int MaxEvents = 8;
    public const int MaxAgeSeconds = 300;
    public const string Key = "test-project";

    // 64 karakter; gerçek bir sağlayıcı önekine benzemez.
    public const string DefaultSecret = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<HubRequest> _requests = new();

    private FakeHub(WebApplication app, string secret)
    {
        _app = app;
        Secret = secret;
    }

    public string Secret { get; }

    public string Url { get; private set; } = string.Empty;

    /// <summary>Her isteğe eklenecek gecikme — yavaş hub benzetimi.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public IReadOnlyList<HubRequest> Requests => _requests.ToArray();

    /// <summary>Kabul edilen isteklerdeki bütün olaylar (hub'ın 8 olay kesmesi uygulanmış).</summary>
    public IReadOnlyList<JsonElement> Events =>
        Requests.Where(r => r.Accepted).SelectMany(r => r.Events).ToArray();

    public IReadOnlyList<JsonElement> EventsOf(string kind) =>
        Events.Where(e => e.GetProperty("kind").GetString() == kind).ToArray();

    public static async Task<FakeHub> StartAsync(string secret = DefaultSecret)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        var hub = new FakeHub(app, secret);

        app.MapPost("/api/i/{key}/server", (Delegate)hub.HandleAsync);

        await app.StartAsync();

        hub.Url = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        return hub;
    }

    private async Task<IResult> HandleAsync(HttpContext context, string key)
    {
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer);
        var raw = buffer.ToArray();

        var request = new HubRequest(
            key,
            raw,
            Encoding.UTF8.GetString(raw),
            Accepts(key, context.Request.Headers, raw),
            context.Request.Headers.ContentType.ToString(),
            context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()));

        _requests.Enqueue(request);

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay);
        }

        // Hub her koşulda 204 döner — başarıda da, geçersiz istekte de.
        return Results.NoContent();
    }

    private bool Accepts(string key, IHeaderDictionary headers, byte[] raw)
    {
        if (key != Key)
        {
            return false;
        }

        var signature = headers["X-VitalWatch-Signature"].ToString();
        var timestamp = headers["X-VitalWatch-Timestamp"].ToString();

        if (signature.Length == 0 || timestamp.Length == 0 || !timestamp.All(char.IsAsciiDigit))
        {
            return false;
        }

        if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - long.Parse(timestamp)) > MaxAgeSeconds)
        {
            return false;
        }

        if (raw.Length == 0 || raw.Length > MaxBodyBytes)
        {
            return false;
        }

        var payload = Encoding.UTF8.GetBytes(timestamp + ".").Concat(raw).ToArray();
        var expected = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), payload)).ToLowerInvariant();

        // hash_equals karşılığı.
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signature));
    }

    /// <summary>Koşul sağlanana kadar bekler; süre dolarsa son durumu döndürür.</summary>
    public async Task<bool> WaitForAsync(Func<FakeHub, bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            if (condition(this))
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition(this);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

public sealed record HubRequest(
    string Key,
    byte[] RawBytes,
    string Raw,
    bool Accepted,
    string ContentType,
    IReadOnlyDictionary<string, string> Headers)
{
    public JsonElement Body => JsonDocument.Parse(RawBytes).RootElement;

    /// <summary>Hub'ın <c>eventsFrom</c> kuralı: <c>events</c> dizisi, en çok 8 olay.</summary>
    public IReadOnlyList<JsonElement> Events =>
        Body.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array
            ? events.EnumerateArray().Take(FakeHub.MaxEvents).ToArray()
            : [];
}
