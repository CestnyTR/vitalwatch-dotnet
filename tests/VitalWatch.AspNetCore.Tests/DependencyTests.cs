using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VitalWatch.Internal;

namespace VitalWatch.Tests;

/// <summary>Bağımlılık listesi: deps.json ayrıştırma, hash, sınırlar, zamanlama, imza, teşhis.</summary>
public sealed class DependencyTests : IAsyncLifetime
{
    private FakeHub _hub = null!;
    private string _dir = null!;
    private string? _offline;

    public async Task InitializeAsync()
    {
        _hub = await FakeHub.StartAsync();
        _dir = Path.Combine(Path.GetTempPath(), "vitalwatch-deps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        _offline = Environment.GetEnvironmentVariable("VITALWATCH_STATUS_OFFLINE");
        Environment.SetEnvironmentVariable("VITALWATCH_STATUS_OFFLINE", "1");
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("VITALWATCH_STATUS_OFFLINE", _offline);
        await _hub.DisposeAsync();

        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Geçici dizin.
        }
    }

    // ------------------------------------------------------------------
    // Yardımcılar
    // ------------------------------------------------------------------

    private const string Sample = """
        {
          "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0", "signature": "" },
          "targets": { ".NETCoreApp,Version=v10.0": {} },
          "libraries": {
            "FaydinX/1.0.0": { "type": "project", "serviceable": false, "sha512": "" },
            "MailKit/4.17.0": { "type": "package", "serviceable": true, "sha512": "sha512-abc", "path": "mailkit/4.17.0" },
            "Newtonsoft.Json/13.0.3": { "type": "package", "serviceable": true, "sha512": "sha512-def", "path": "newtonsoft.json/13.0.3" },
            "newtonsoft.json/13.0.3": { "type": "package" },
            "BouncyCastle.Cryptography/2.5.1": { "type": "package" },
            "Legacy.Dll/1.2.3.4": { "type": "reference", "serviceable": false, "sha512": "" },
            "runtimepack.Microsoft.NETCore.App.Runtime.linux-x64/10.0.0": { "type": "runtimepack" },
            "Broken": { "type": "package" },
            "NoVersion/": { "type": "package" },
          }
        }
        """;

    private static string Libraries(IEnumerable<(string Name, string Version)> packages) =>
        "{\"libraries\":{" +
        string.Join(",", packages.Select(p => $"\"{p.Name}/{p.Version}\":{{\"type\":\"package\"}}")) +
        "}}";

    private string WriteDeps(string content, string name = "FaydinX.deps.json")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);

        return path;
    }

    private Settings HubSettings(Action<VitalWatchOptions>? configure = null) => Settings.Resolve(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["VitalWatch:Url"] = _hub.Url,
            ["VitalWatch:Key"] = FakeHub.Key,
            ["VitalWatch:Secret"] = _hub.Secret,
            ["VitalWatch:Release"] = "abc123def456",
        }).Build(),
        _ => null,
        "Production",
        null,
        configure);

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    // ------------------------------------------------------------------
    // Ayrıştırma ve hash
    // ------------------------------------------------------------------

    [Fact]
    public void Only_package_libraries_are_taken_deduped_and_sorted()
    {
        var manifest = DependencyManifest.Parse(Encoding.UTF8.GetBytes(Sample), "FaydinX.deps.json");

        Assert.Equal(ManifestState.Found, manifest.State);
        Assert.Equal("FaydinX.deps.json", manifest.FileName);
        Assert.Equal(
            ["BouncyCastle.Cryptography@2.5.1", "MailKit@4.17.0", "Newtonsoft.Json@13.0.3"],
            manifest.Packages.Select(p => $"{p.Name}@{p.Version}").ToArray());
    }

    [Fact]
    public void Hash_is_sha256_of_canonical_lines_and_independent_of_input_order()
    {
        var a = DependencyManifest.Parse(Encoding.UTF8.GetBytes(Libraries([("Zeta", "1.0.0"), ("alpha", "2.0.0"), ("Alpha", "1.0.0")])), "x.deps.json");
        var b = DependencyManifest.Parse(Encoding.UTF8.GetBytes(Libraries([("Alpha", "1.0.0"), ("Zeta", "1.0.0"), ("alpha", "2.0.0")])), "x.deps.json");

        Assert.Equal("Alpha@1.0.0\nalpha@2.0.0\nZeta@1.0.0\n",
            string.Concat(a.Packages.Select(p => $"{p.Name}@{p.Version}\n")));
        Assert.Equal(Sha256("Alpha@1.0.0\nalpha@2.0.0\nZeta@1.0.0\n"), a.Hash);
        Assert.Equal(a.Hash, b.Hash);
        Assert.Matches("^[0-9a-f]{64}$", a.Hash);
    }

    [Fact]
    public void Case_insensitive_order_compares_lowercase_like_the_other_sdks()
    {
        // OrdinalIgnoreCase büyük harfe çevirir ve "Ab"yi "A_b"den önce koyardı; JS/Python/PHP
        // küçük harfle karşılaştırır: '_' (0x5F) < 'b' (0x62).
        var manifest = DependencyManifest.Parse(Encoding.UTF8.GetBytes(Libraries([("Ab", "1.0.0"), ("A_b", "1.0.0")])), "x.deps.json");

        Assert.Equal(["A_b", "Ab"], manifest.Packages.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void List_is_capped_at_5000_after_sorting()
    {
        var packages = Enumerable.Range(0, 6000).Select(i => ($"Pkg{i:D5}", "1.0.0"));
        var manifest = DependencyManifest.Parse(Encoding.UTF8.GetBytes(Libraries(packages)), "x.deps.json");

        Assert.Equal(DependencyManifest.MaxPackages, manifest.Packages.Count);
        Assert.Equal("Pkg00000", manifest.Packages[0].Name);
        Assert.Equal("Pkg04999", manifest.Packages[^1].Name);
    }

    [Fact]
    public void Missing_or_invalid_file_does_not_throw()
    {
        Assert.Equal(ManifestState.NotFound, DependencyManifest.Load(Path.Combine(_dir, "nope.deps.json")).State);
        Assert.Equal(ManifestState.NotFound, DependencyManifest.Load(null).State);
        Assert.Equal(ManifestState.Unreadable, DependencyManifest.Load(WriteDeps("{ not json", "bad.deps.json")).State);
        Assert.Equal(ManifestState.Unreadable, DependencyManifest.Load(WriteDeps("[]", "array.deps.json")).State);
    }

    [Fact]
    public void Real_deps_json_of_the_test_project_is_readable()
    {
        // Test derlemesinin kendi deps.json'ı gerçek SDK çıktısı: xunit paketleri içinde olmalı.
        var path = Path.Combine(AppContext.BaseDirectory, "VitalWatch.AspNetCore.Tests.deps.json");
        var manifest = DependencyManifest.Load(path);

        Assert.True(manifest.Found);
        Assert.Contains(manifest.Packages, p => p.Name == "xunit");
        Assert.DoesNotContain(manifest.Packages, p => p.Name == "VitalWatch.AspNetCore"); // project
    }

    // ------------------------------------------------------------------
    // Gövde ve imza
    // ------------------------------------------------------------------

    [Fact]
    public async Task Body_carries_identity_and_list_and_is_signed_like_server()
    {
        using var reporter = new Reporter(HubSettings());
        using var deps = new DependencyReporter(reporter, WriteDeps(Sample));

        var result = await deps.SendAsync(deps.Read());

        Assert.True(result.Sent);
        Assert.Equal(204, result.Status);

        var request = Assert.Single(_hub.DepsRequests);
        Assert.Empty(_hub.Requests); // /server'a hiçbir şey gitmedi

        // Sahte hub imzayı /server ile aynı kuralla doğruluyor.
        Assert.True(request.Accepted);
        Assert.Equal(FakeHub.Key, request.Key);
        Assert.StartsWith("application/json", request.ContentType);
        Assert.StartsWith("sha256=", request.Headers["X-VitalWatch-Signature"]);
        Assert.False(request.Headers.ContainsKey("traceparent"));

        var body = request.Body;
        Assert.Equal("production", body.GetProperty("env").GetString());
        Assert.Equal("server", body.GetProperty("source").GetString());
        Assert.Equal("abc123def456", body.GetProperty("release").GetString());
        Assert.Equal("dotnet", body.GetProperty("runtime").GetString());
        Assert.Equal(Environment.Version.ToString(), body.GetProperty("runtime_version").GetString());
        Assert.Equal("0.2.0", body.GetProperty("sdk_version").GetString());
        Assert.False(body.TryGetProperty("events", out _));

        var list = body.GetProperty("dependencies");
        Assert.Equal(1, list.GetProperty("v").GetInt32());
        Assert.Equal("NuGet", list.GetProperty("ecosystem").GetString());
        Assert.Equal("FaydinX.deps.json", list.GetProperty("manifest").GetString());
        Assert.Equal(Sha256("BouncyCastle.Cryptography@2.5.1\nMailKit@4.17.0\nNewtonsoft.Json@13.0.3\n"), list.GetProperty("hash").GetString());

        var packages = list.GetProperty("packages").EnumerateArray().ToArray();
        Assert.Equal(3, packages.Length);
        Assert.Equal("MailKit", packages[1].GetProperty("name").GetString());
        Assert.Equal("4.17.0", packages[1].GetProperty("version").GetString());

        // Yalnızca ad + sürüm: yol, sha512, lisans gitmez.
        Assert.All(packages, p => Assert.Equal(["name", "version"], p.EnumerateObject().Select(x => x.Name).ToArray()));
        Assert.DoesNotContain("sha512", request.Raw);
        Assert.DoesNotContain(_dir, request.Raw);
    }

    [Fact]
    public async Task Wrong_secret_is_rejected_by_the_hub_like_server()
    {
        using var reporter = new Reporter(HubSettings(o => o.Secret = new string('x', 64)));
        using var deps = new DependencyReporter(reporter, WriteDeps(Sample));

        Assert.True((await deps.SendAsync(deps.Read())).Sent); // 204 her durumda…
        Assert.False(Assert.Single(_hub.DepsRequests).Accepted); // …ama hub kabul etmedi.
    }

    [Fact]
    public async Task Body_over_500_kb_is_not_sent_and_warns_once()
    {
        // 3000 paket × ~200 karakter ad ≈ 650 KB.
        var packages = Enumerable.Range(0, 3000).Select(i => ($"Very.Long.Package.Name.{i:D4}." + new string('x', 180), "1.0.0"));
        var logger = new ListLogger();

        using var reporter = new Reporter(HubSettings());
        using var deps = new DependencyReporter(reporter, WriteDeps(Libraries(packages)), logger: logger);

        Assert.True(DependencyReporter.Encode(deps.Read(), reporter.Settings).Length > DependencyReporter.MaxBodyBytes);

        var first = await deps.SendAsync(deps.Read());
        var second = await deps.TickAsync(CancellationToken.None);

        Assert.False(first.Sent);
        Assert.Equal("body-too-large", first.Error);
        Assert.False(second!.Value.Sent);
        Assert.Empty(_hub.DepsRequests);

        var warning = Assert.Single(logger.Messages);
        Assert.Contains("dependency list was not sent", warning);
    }

    // ------------------------------------------------------------------
    // Zamanlama
    // ------------------------------------------------------------------

    [Fact]
    public async Task Schedule_first_after_60s_then_on_hash_change_and_every_24h()
    {
        var clock = new ManualTimeProvider();
        var path = WriteDeps(Libraries([("MailKit", "4.17.0")]));

        using var reporter = new Reporter(HubSettings());
        using var deps = new DependencyReporter(reporter, path, clock);

        deps.Start();
        Assert.True(await clock.WaitForPendingAsync());

        // 59 sn: henüz yok.
        clock.Advance(TimeSpan.FromSeconds(59));
        await Task.Delay(100);
        Assert.Empty(_hub.DepsRequests);

        // 60 sn: ilk gönderim.
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await _hub.WaitForAsync(h => h.DepsRequests.Count == 1));
        Assert.True(await clock.WaitForPendingAsync());

        // Bir saat sonra liste aynı: gönderilmez.
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True(await clock.WaitForPendingAsync());
        await Task.Delay(100);
        Assert.Single(_hub.DepsRequests);

        // Liste değişti: bir sonraki denetimde gönderilir.
        File.WriteAllText(path, Libraries([("MailKit", "4.17.0"), ("Newtonsoft.Json", "13.0.3")]));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True(await _hub.WaitForAsync(h => h.DepsRequests.Count == 2));

        var firstHash = _hub.DepsRequests[0].Body.GetProperty("dependencies").GetProperty("hash").GetString();
        var secondHash = _hub.DepsRequests[1].Body.GetProperty("dependencies").GetProperty("hash").GetString();
        Assert.NotEqual(firstHash, secondHash);

        // Değişmeyen liste 24 saatte bir yeniden gider: 23 saat sessiz, 24. saatte gönderim.
        for (var hour = 1; hour <= 23; hour++)
        {
            Assert.True(await clock.WaitForPendingAsync());
            clock.Advance(TimeSpan.FromHours(1));
        }

        Assert.True(await clock.WaitForPendingAsync());
        await Task.Delay(100);
        Assert.Equal(2, _hub.DepsRequests.Count);

        clock.Advance(TimeSpan.FromHours(1));
        Assert.True(await _hub.WaitForAsync(h => h.DepsRequests.Count == 3));
        Assert.All(_hub.DepsRequests, r => Assert.True(r.Accepted));

        // Kapanış: döngü iptalle biter.
        deps.Stop();
        await deps.Loop!.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Failed_send_is_retried_on_the_next_check()
    {
        var clock = new ManualTimeProvider();

        // Hub'a ulaşılamaz: kapalı bir port.
        using var reporter = new Reporter(HubSettings(o => { o.Url = "http://127.0.0.1:1"; o.Timeout = 200; }));
        using var deps = new DependencyReporter(reporter, WriteDeps(Sample), clock);

        var first = await deps.TickAsync(CancellationToken.None);
        Assert.NotNull(first);
        Assert.False(first!.Value.Sent);

        // Gönderilemediği için "gönderildi" sayılmadı: bir sonraki tur yine dener.
        var second = await deps.TickAsync(CancellationToken.None);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task Disabled_option_starts_nothing_and_sends_nothing()
    {
        var clock = new ManualTimeProvider();

        using var reporter = new Reporter(HubSettings(o => o.Dependencies = false));
        using var deps = new DependencyReporter(reporter, WriteDeps(Sample), clock);

        deps.Start();

        Assert.Null(deps.Loop);
        Assert.Equal(0, clock.Pending);
        Assert.Null(await deps.TickAsync(CancellationToken.None));
        Assert.Empty(_hub.DepsRequests);
    }

    [Fact]
    public async Task Missing_deps_json_sends_nothing()
    {
        using var reporter = new Reporter(HubSettings());
        using var deps = new DependencyReporter(reporter, Path.Combine(_dir, "missing.deps.json"));

        Assert.Null(await deps.TickAsync(CancellationToken.None));
        Assert.Empty(_hub.DepsRequests);
    }

    [Fact]
    public async Task Hosted_service_starts_the_loop_and_stops_it_on_shutdown()
    {
        var clock = new ManualTimeProvider();
        var path = WriteDeps(Sample);
        DependencyReporter? deps = null;

        var app = await TestApp.StartAsync(_hub, _ => { }, options: o => o.Dependencies = true, services: s =>
            s.AddSingleton(sp => deps = new DependencyReporter(sp.GetRequiredService<Reporter>(), path, clock)));

        Assert.NotNull(deps);
        Assert.NotNull(deps!.Loop);
        Assert.True(await clock.WaitForPendingAsync());

        clock.Advance(DependencyReporter.InitialDelay);
        Assert.True(await _hub.WaitForAsync(h => h.DepsRequests.Count == 1));
        Assert.True(_hub.DepsRequests[0].Accepted);

        await app.StopAsync();
        await deps.Loop.WaitAsync(TimeSpan.FromSeconds(5));
        await app.DisposeAsync();
    }

    [Fact]
    public async Task Hosted_service_does_not_start_the_loop_when_disabled()
    {
        DependencyReporter? deps = null;

        await using var app = await TestApp.StartAsync(_hub, _ => { }, services: s =>
            s.AddSingleton(sp => deps = new DependencyReporter(sp.GetRequiredService<Reporter>(), WriteDeps(Sample), new ManualTimeProvider())));

        Assert.NotNull(deps);
        Assert.Null(deps!.Loop);
    }

    // ------------------------------------------------------------------
    // Teşhis komutu
    // ------------------------------------------------------------------

    private WebApplication BuildStatusApp(string[] args, string? depsPath, Action<VitalWatchOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["VitalWatch:Url"] = _hub.Url,
            ["VitalWatch:Key"] = FakeHub.Key,
            ["VitalWatch:Secret"] = _hub.Secret,
        });
        builder.Services.AddVitalWatch(o =>
        {
            o.HeartbeatInterval = TimeSpan.Zero;
            configure?.Invoke(o);
        });
        builder.Services.AddSingleton(sp => new DependencyReporter(sp.GetRequiredService<Reporter>(), depsPath));

        return builder.Build();
    }

    private async Task<(int ExitCode, string Text)> RunStatus(string[] args, string? depsPath, Action<VitalWatchOptions>? configure = null)
    {
        await using var app = BuildStatusApp(args, depsPath, configure);
        var output = new StringWriter();
        var previous = Environment.ExitCode;

        Assert.True(await app.RunVitalWatchStatusAsync(args, output));
        var exitCode = Environment.ExitCode;
        Environment.ExitCode = previous;

        return (exitCode, output.ToString());
    }

    [Fact]
    public async Task Status_shows_the_dependency_line()
    {
        var (code, text) = await RunStatus(["--vitalwatch-status"], WriteDeps(Sample));

        Assert.Equal(0, code);
        Assert.Contains("Dependencies       FaydinX.deps.json · 3 packages", text);
        Assert.Empty(_hub.DepsRequests);
    }

    [Fact]
    public async Task Status_reports_missing_deps_json_and_disabled()
    {
        var (_, missing) = await RunStatus(["--vitalwatch-status"], Path.Combine(_dir, "missing.deps.json"));
        Assert.Contains("Dependencies       deps.json not found", missing);

        var (_, disabled) = await RunStatus(["--vitalwatch-status"], WriteDeps(Sample), o => o.Dependencies = false);
        Assert.Contains("Dependencies       disabled", disabled);
    }

    [Fact]
    public async Task Status_deps_sends_immediately_and_prints_http_status()
    {
        var (code, text) = await RunStatus(["--vitalwatch-status", "--deps"], WriteDeps(Sample));

        Assert.True(code == 0, text);
        Assert.Contains("✓ Dependency list sent (HTTP 204, 3 packages).", text);

        var request = Assert.Single(_hub.DepsRequests);
        Assert.True(request.Accepted);
        Assert.Empty(_hub.Requests);
    }

    [Fact]
    public async Task Status_deps_fails_when_disabled_or_missing()
    {
        var (disabledCode, disabled) = await RunStatus(["--vitalwatch-status", "--deps"], WriteDeps(Sample), o => o.Dependencies = false);
        Assert.Equal(1, disabledCode);
        Assert.Contains("✗ Dependency list could NOT be sent: disabled", disabled);

        var (missingCode, missing) = await RunStatus(["--vitalwatch-status", "--deps"], Path.Combine(_dir, "missing.deps.json"));
        Assert.Equal(1, missingCode);
        Assert.Contains("✗ Dependency list could NOT be sent: deps.json not found.", missing);

        Assert.Empty(_hub.DepsRequests);
    }

    [Fact]
    public async Task Status_test_does_not_send_dependencies()
    {
        var (code, text) = await RunStatus(["--vitalwatch-status", "--test", "--vitalwatch"], WriteDeps(Sample));

        Assert.True(code == 0, text);
        Assert.Contains("✓ Test event sent (HTTP 204)", text);
        Assert.Empty(_hub.DepsRequests);
        Assert.Equal(2, _hub.Requests.Count);
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
            {
                Messages.Add(formatter(state, exception));
            }
        }
    }
}
