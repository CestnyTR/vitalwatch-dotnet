using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VitalWatch.Internal;

namespace VitalWatch.Tests;

/// <summary>Arka plan servisleri, kapalı mod, hub arızası, canlılık, elle bildirim.</summary>
public sealed class HostTests : IAsyncLifetime
{
    private FakeHub _hub = null!;

    public async Task InitializeAsync() => _hub = await FakeHub.StartAsync();

    public async Task DisposeAsync() => await _hub.DisposeAsync();

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    public sealed class CrashingService : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(50, stoppingToken);
            throw new InvalidOperationException("Gece işi düştü, bildirim 05321234567 numarasına");
        }
    }

    [Fact]
    public async Task Crashed_background_service_is_job_failed()
    {
        await using var app = await TestApp.StartAsync(_hub, _ => { }, services: s =>
        {
            s.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
            s.AddHostedService<CrashingService>();
        });

        Assert.True(await _hub.WaitForAsync(h => h.EventsOf("job_failed").Count == 1));

        var e = _hub.EventsOf("job_failed")[0];
        Assert.Equal(typeof(CrashingService).FullName, Str(e, "route"));
        Assert.Equal("Gece işi düştü, bildirim [phone] numarasına", Str(e, "msg"));
        Assert.Equal("System.InvalidOperationException", Str(e, "exception_class"));
        Assert.False(e.TryGetProperty("method", out _));
    }

    [Fact]
    public async Task Stop_host_behavior_logs_twice_but_reports_once()
    {
        var app = await TestApp.StartAsync(_hub, _ => { }, services: s =>
        {
            s.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost);
            s.AddHostedService<CrashingService>();
        });

        // Host kendini durdurur; kapanışta kuyruk boşaltılır.
        var stopping = app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        await Task.Delay(Timeout.Infinite, stopping).ContinueWith(_ => { });
        await app.StopAsync();
        await app.DisposeAsync();

        Assert.True(await _hub.WaitForAsync(h => h.EventsOf("job_failed").Count >= 1));
        await Task.Delay(200);
        Assert.Single(_hub.EventsOf("job_failed"));
    }

    [Fact]
    public async Task Other_log_categories_are_ignored()
    {
        await using var app = await TestApp.StartAsync(_hub, _ => { });

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MyApp.Orders");
        logger.LogError(new InvalidOperationException("uygulama kendi günlüğüne yazdı"), "hata");

        var hostLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(VitalWatchLoggerProvider.HostCategory);
        hostLogger.LogWarning(new InvalidOperationException("uyarı seviyesi"), "uyarı");

        await Task.Delay(300);
        Assert.Empty(_hub.Events);
    }

    [Fact]
    public async Task Manual_report_static_and_injected()
    {
        await using var app = await TestApp.StartAsync(_hub, _ => { });

        var error = new TimeoutException("ödeme servisi yanıt vermedi");

        Assert.True(VitalWatchSdk.Enabled);
        Assert.True(VitalWatchSdk.Report(error));
        Assert.False(VitalWatchSdk.Report(error)); // aynı nesne ikinci kez gitmez

        var injected = app.Services.GetRequiredService<IVitalWatch>();
        Assert.True(injected.Report(new InvalidOperationException("komut"), "command_failed"));
        Assert.True(injected.Report(new InvalidOperationException("tanınmayan tür"), "weird_kind"));

        Assert.True(await _hub.WaitForAsync(h => h.Events.Count == 3));
        Assert.Single(_hub.EventsOf("command_failed"));
        Assert.Equal(2, _hub.EventsOf("exception").Count);
        Assert.Equal("System.TimeoutException", Str(_hub.EventsOf("exception")[0], "exception_class"));
    }

    [Fact]
    public async Task Ignored_types_are_not_reported()
    {
        await using var app = await TestApp.StartAsync(_hub, _ => { }, options: o => o.Ignore.Add(typeof(ArgumentException)));

        Assert.False(VitalWatchSdk.Report(new ArgumentNullException("x")));
        Assert.True(VitalWatchSdk.Report(new InvalidOperationException("x")));

        Assert.True(await _hub.WaitForAsync(h => h.Events.Count == 1));
    }

    [Fact]
    public async Task Many_events_arrive_in_batches_within_hub_limits()
    {
        await using var app = await TestApp.StartAsync(_hub, _ => { });

        for (var i = 0; i < 30; i++)
        {
            Assert.True(VitalWatchSdk.Report(new InvalidOperationException($"hata {i}")));
        }

        Assert.True(await _hub.WaitForAsync(h => h.EventsOf("exception").Count == 30));

        Assert.All(_hub.Requests, r =>
        {
            Assert.True(r.Accepted);
            Assert.True(r.RawBytes.Length <= FakeHub.MaxBodyBytes);
            Assert.InRange(r.Body.GetProperty("events").GetArrayLength(), 1, FakeHub.MaxEvents);
            Assert.Equal("application/json", r.ContentType.Split(';')[0]);
        });

        // Toplu gönderim: 30 olay için 30 istek değil.
        Assert.True(_hub.Requests.Count <= 8, $"{_hub.Requests.Count} istek");
    }

    [Fact]
    public async Task Heartbeat_is_sent_at_startup_with_identity()
    {
        await using var app = await TestApp.StartAsync(_hub, _ => { }, options: o => o.HeartbeatInterval = TimeSpan.FromHours(8));

        Assert.True(await _hub.WaitForAsync(h => h.Requests.Count == 1));

        var request = _hub.Requests[0];
        Assert.True(request.Accepted);
        Assert.Equal(0, request.Body.GetProperty("events").GetArrayLength());
        Assert.Equal("dotnet", request.Body.GetProperty("runtime").GetString());
        Assert.Equal("0.2.0", request.Body.GetProperty("sdk_version").GetString());
        Assert.Equal("production", request.Body.GetProperty("env").GetString());
        Assert.Equal($"/api/i/{FakeHub.Key}/server", "/api/i/" + request.Key + "/server");
        Assert.False(request.Headers.ContainsKey("traceparent"));
    }

    [Fact]
    public async Task Disabled_installs_nothing()
    {
        await using var app = await TestApp.StartAsync(_hub, app =>
        {
            app.MapGet("/boom", () => MiddlewareTests.Throw("kapalıyken"));
            app.MapGet("/fail", () => Microsoft.AspNetCore.Http.Results.StatusCode(500));
        }, configuration: new Dictionary<string, string?> { ["VitalWatch:Enabled"] = "false" },
            services: s => s.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(new MiddlewareTests.OuterCatch(_ => { })));

        Assert.Null(app.Services.GetService<Reporter>());
        Assert.Null(app.Services.GetService<Settings>());
        Assert.DoesNotContain(app.Services.GetServices<IHostedService>(), s => s is VitalWatchHostedService);
        Assert.DoesNotContain(app.Services.GetServices<ILoggerProvider>(), p => p is VitalWatchLoggerProvider);

        var injected = app.Services.GetRequiredService<IVitalWatch>();
        Assert.False(injected.Enabled);
        Assert.False(injected.Report(new InvalidOperationException("x")));
        Assert.False(VitalWatchSdk.Enabled);
        Assert.False(VitalWatchSdk.Report(new InvalidOperationException("x")));

        await app.Client().GetAsync("/boom");
        await app.Client().GetAsync("/fail");

        await Task.Delay(400);
        Assert.Empty(_hub.Requests);
    }

    [Fact]
    public async Task Incomplete_configuration_installs_no_middleware_and_sends_nothing()
    {
        await using var app = await TestApp.StartAsync(_hub, app =>
        {
            app.MapGet("/fail", () => Microsoft.AspNetCore.Http.Results.StatusCode(500));
        }, configuration: new Dictionary<string, string?> { ["VitalWatch:Secret"] = "" });

        Assert.False(app.Services.GetRequiredService<IVitalWatch>().Enabled);

        await app.Client().GetAsync("/fail");
        await Task.Delay(300);
        Assert.Empty(_hub.Requests);
    }

    [Fact]
    public async Task Incomplete_configuration_logs_one_startup_warning()
    {
        var logs = new CapturingLoggerProvider();

        await using var app = await TestApp.StartAsync(_hub, _ => { }, configuration: new Dictionary<string, string?>
        {
            ["VitalWatch:Secret"] = "abcde",
            ["VitalWatch:Key"] = "",
        }, services: s => s.AddSingleton<ILoggerProvider>(logs));

        var warnings = logs.Entries.Where(e => e.Category == "VitalWatch").ToArray();
        var warning = Assert.Single(warnings);

        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("VITALWATCH_KEY not set", warning.Message);
        Assert.Contains("must be 64 characters, got 5", warning.Message);
        Assert.Contains("--vitalwatch-status", warning.Message);
        Assert.DoesNotContain("abcde", warning.Message); // secret'in kendisi günlüğe yazılmaz
    }

    [Fact]
    public async Task Complete_configuration_logs_nothing()
    {
        var logs = new CapturingLoggerProvider();

        await using var app = await TestApp.StartAsync(_hub, _ => { }, services: s => s.AddSingleton<ILoggerProvider>(logs));

        Assert.DoesNotContain(logs.Entries, e => e.Category == "VitalWatch");
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<(string Category, LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Entries.Enqueue((category, logLevel, formatter(state, exception)));
        }
    }

    [Fact]
    public async Task Unreachable_hub_neither_throws_nor_blocks()
    {
        // Kapalı bir port: bağlantı reddedilir.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        await using var app = await TestApp.StartAsync(null, app =>
        {
            app.MapGet("/fail", () => Microsoft.AspNetCore.Http.Results.StatusCode(500));
        }, configuration: new Dictionary<string, string?>
        {
            ["VitalWatch:Url"] = $"http://127.0.0.1:{port}",
            ["VitalWatch:Secret"] = FakeHub.DefaultSecret,
        });

        var watch = Stopwatch.StartNew();
        var response = await app.Client().GetAsync("/fail");
        watch.Stop();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"{watch.ElapsedMilliseconds} ms");

        var reporter = app.Services.GetRequiredService<IVitalWatch>();
        Assert.True(await reporter.FlushAsync(TimeSpan.FromSeconds(5)));

        var result = await reporter.HeartbeatAsync();
        Assert.False(result.Sent);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Slow_hub_does_not_delay_requests_and_full_queue_drops()
    {
        _hub.Delay = TimeSpan.FromSeconds(3);

        await using var app = await TestApp.StartAsync(_hub, app =>
        {
            app.MapGet("/fail", () => Microsoft.AspNetCore.Http.Results.StatusCode(500));
        }, options: o => o.Timeout = 200);

        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 5; i++)
        {
            await app.Client().GetAsync("/fail");
        }

        Assert.True(watch.ElapsedMilliseconds < 1500, $"{watch.ElapsedMilliseconds} ms");

        // Kuyruk sınırlı: gönderici takılıyken fazlası düşürülür, çağıran beklemez.
        watch.Restart();
        var accepted = 0;
        for (var i = 0; i < Reporter.QueueSize * 3; i++)
        {
            if (VitalWatchSdk.Report(new InvalidOperationException($"fırtına {i}")))
            {
                accepted++;
            }
        }

        Assert.True(watch.ElapsedMilliseconds < 500, $"{watch.ElapsedMilliseconds} ms");
        Assert.True(accepted < Reporter.QueueSize * 3);
        Assert.True(((Reporter)app.Services.GetRequiredService<IVitalWatch>()).Dropped > 0);

        _hub.Delay = TimeSpan.Zero;
    }

    [Fact]
    public void Job_name_is_found_from_async_state_machine()
    {
        // ExecuteAsync'ı doğrudan çalıştırıp istisnayı alıyoruz; host'un gördüğü stack bu.
        var captured = Record.Exception(() => RunExecute().GetAwaiter().GetResult());

        Assert.Equal(typeof(CrashingService).FullName, VitalWatchLoggerProvider.JobName(captured!));
        Assert.Equal("host", VitalWatchLoggerProvider.JobName(new InvalidOperationException("stack yok")));

        static Task RunExecute()
        {
            var service = new CrashingService();
            var method = typeof(CrashingService).GetMethod("ExecuteAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

            return (Task)method.Invoke(service, [CancellationToken.None])!;
        }
    }
}
