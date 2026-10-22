using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Süreç geneli durum var (DiagnosticListener aboneliği, statik yüz); testler sırayla koşar.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace VitalWatch.Tests;

/// <summary>TestServer üzerinde VitalWatch kurulu küçük bir uygulama.</summary>
internal static class TestApp
{
    public static async Task<WebApplication> StartAsync(
        FakeHub? hub,
        Action<WebApplication> pipeline,
        Action<VitalWatchOptions>? options = null,
        IDictionary<string, string?>? configuration = null,
        Action<IServiceCollection>? services = null,
        bool useVitalWatch = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
        });

        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var values = new Dictionary<string, string?>
        {
            ["VitalWatch:Url"] = hub?.Url,
            ["VitalWatch:Key"] = FakeHub.Key,
            ["VitalWatch:Secret"] = hub?.Secret,
        };

        foreach (var (key, value) in configuration ?? new Dictionary<string, string?>())
        {
            values[key] = value;
        }

        builder.Configuration.AddInMemoryCollection(values);

        builder.Services.AddVitalWatch(o =>
        {
            // Testlerde canlılık isteği gürültü olur; ayrı testte açılıyor.
            o.HeartbeatInterval = TimeSpan.Zero;
            options?.Invoke(o);
        });

        services?.Invoke(builder.Services);

        var app = builder.Build();

        if (useVitalWatch)
        {
            app.UseVitalWatch();
        }

        pipeline(app);

        await app.StartAsync();

        return app;
    }

    public static HttpClient Client(this WebApplication app) => app.GetTestClient();
}
