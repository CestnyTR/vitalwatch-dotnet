using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace VitalWatch.Tests;

public sealed class StatusTests : IAsyncLifetime
{
    private FakeHub _hub = null!;
    private string? _offline;

    public async Task InitializeAsync()
    {
        _hub = await FakeHub.StartAsync();

        // NuGet'e gidilmesin.
        _offline = Environment.GetEnvironmentVariable("VITALWATCH_STATUS_OFFLINE");
        Environment.SetEnvironmentVariable("VITALWATCH_STATUS_OFFLINE", "1");
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("VITALWATCH_STATUS_OFFLINE", _offline);
        await _hub.DisposeAsync();
    }

    private WebApplication Build(string[] args, IDictionary<string, string?> values)
    {
        // Bayraklar komut satırı yapılandırmasına da girer; host kurulumu bozulmamalı.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(values);
        builder.Services.AddVitalWatch(o => o.HeartbeatInterval = TimeSpan.Zero);

        return builder.Build();
    }

    [Fact]
    public async Task Without_flag_does_nothing()
    {
        await using var app = Build([], new Dictionary<string, string?>());

        Assert.False(await app.RunVitalWatchStatusAsync([], new StringWriter()));
    }

    [Fact]
    public async Task Valid_configuration_with_test_sends_a_real_event()
    {
        string[] args = ["--vitalwatch-status", "--test", "--vitalwatch"];

        await using var app = Build(args, new Dictionary<string, string?>
        {
            ["VitalWatch:Url"] = _hub.Url,
            ["VitalWatch:Key"] = FakeHub.Key,
            ["VitalWatch:Secret"] = _hub.Secret,
            ["VitalWatch:Release"] = "v9.9.9",
        });

        var output = new StringWriter();
        var previous = Environment.ExitCode;

        Assert.True(await app.RunVitalWatchStatusAsync(args, output));

        var text = output.ToString();
        Assert.True(Environment.ExitCode == 0, text);
        Assert.Contains("✓ Configuration OK.", text);
        Assert.Contains("✓ Test event sent (HTTP 204)", text);
        Assert.Contains("✓ Liveness request sent (HTTP 204)", text);
        Assert.Contains("Secret length      64 characters", text);
        Assert.Contains("Release tag        v9.9.9 (source: appsettings)", text);
        Assert.DoesNotContain(_hub.Secret, text);

        Assert.Equal(2, _hub.Requests.Count);
        Assert.All(_hub.Requests, r => Assert.True(r.Accepted));
        Assert.Single(_hub.EventsOf("exception"));

        Environment.ExitCode = previous;
    }

    [Fact]
    public async Task Broken_configuration_lists_problems_and_fails()
    {
        string[] args = ["--vitalwatch-status"];

        await using var app = Build(args, new Dictionary<string, string?>
        {
            ["VitalWatch:Url"] = "http://hub.example",
            ["VitalWatch:Secret"] = "eksik-kopyalanmis",
            ["VitalWatch:Env"] = "qa",
        });

        var output = new StringWriter();
        var previous = Environment.ExitCode;

        Assert.True(await app.RunVitalWatchStatusAsync(args, output));

        var text = output.ToString();
        Assert.Equal(1, Environment.ExitCode);
        Assert.Contains("VITALWATCH_KEY not set", text);
        Assert.Contains("must be 64 characters, got 17", text);
        Assert.Contains("http://", text);
        Assert.Contains("\"qa\"", text);

        Environment.ExitCode = previous;
    }

    [Fact]
    public async Task Disabled_is_reported()
    {
        string[] args = ["--vitalwatch-status"];

        await using var app = Build(args, new Dictionary<string, string?> { ["VitalWatch:Enabled"] = "false" });

        var output = new StringWriter();
        var previous = Environment.ExitCode;

        Assert.True(await app.RunVitalWatchStatusAsync(args, output));
        Assert.Contains("VITALWATCH_ENABLED=false", output.ToString());

        Environment.ExitCode = previous;
    }

    [Theory]
    [InlineData("0.2.0", "0.1.0", true)]
    [InlineData("0.10.0", "0.9.0", true)]
    [InlineData("0.1.0", "0.1.0", false)]
    [InlineData("0.1.0", "0.1.0-beta.1", true)]
    [InlineData("0.1.0-beta.2", "0.1.0", false)]
    [InlineData("çöp", "0.1.0", false)]
    public void Version_comparison(string latest, string installed, bool expected)
    {
        Assert.Equal(expected, VitalWatchStatusExtensions.IsNewer(latest, installed));
    }
}
