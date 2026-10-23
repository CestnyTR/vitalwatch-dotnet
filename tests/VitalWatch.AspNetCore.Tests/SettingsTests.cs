using Microsoft.Extensions.Configuration;
using VitalWatch.Internal;

namespace VitalWatch.Tests;

public class SettingsTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static Func<string, string?> Env(params (string Key, string Value)[] values)
    {
        var map = values.ToDictionary(v => v.Key, v => v.Value);

        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    private static Settings Resolve(IConfiguration? config = null, Func<string, string?>? env = null, string? hostEnv = "Production", Action<VitalWatchOptions>? configure = null) =>
        Settings.Resolve(config, env ?? Env(), hostEnv, null, configure);

    [Theory]
    [InlineData(null, 2000)]
    [InlineData(2.0, 2000)] // 100'ün altı saniye
    [InlineData(0.5, 500)]
    [InlineData(0.01, 100)] // 10 ms → alt sınır
    [InlineData(50.0, 10000)] // 50 sn → üst sınır
    [InlineData(99.0, 10000)]
    [InlineData(100.0, 100)] // 100 ve üstü milisaniye
    [InlineData(2000.0, 2000)]
    [InlineData(99999.0, 10000)]
    [InlineData(0.0, 2000)]
    [InlineData(-5.0, 2000)]
    [InlineData(double.NaN, 2000)]
    [InlineData(double.PositiveInfinity, 2000)]
    public void Timeout_rule_matches_siblings(double? input, int expected)
    {
        Assert.Equal(expected, Settings.TimeoutMilliseconds(input));
    }

    [Theory]
    [InlineData("2", 2000)]
    [InlineData("1500", 1500)]
    [InlineData("abc", 2000)]
    [InlineData("0,5", 2000)] // virgül geçersiz — kültürden bağımsız
    public void Timeout_from_environment_is_parsed_invariantly(string value, int expected)
    {
        Assert.Equal(expected, Resolve(env: Env(("VITALWATCH_TIMEOUT", value))).TimeoutMs);
    }

    [Fact]
    public void Process_environment_wins_over_appsettings_and_code_wins_over_both()
    {
        var config = Config(("VitalWatch:Url", "https://from-appsettings"), ("VitalWatch:Key", "k1"), ("VitalWatch:SlowRequestMs", "300"));
        var env = Env(("VITALWATCH_URL", "https://from-env"), ("VITALWATCH_SLOW_REQUEST_MS", "700"));

        var fromEnv = Resolve(config, env);
        Assert.Equal("https://from-env", fromEnv.Url);
        Assert.Equal("k1", fromEnv.Key);
        Assert.Equal(700, fromEnv.SlowRequestMs);

        var fromCode = Resolve(config, env, configure: o => o.Url = "https://from-code");
        Assert.Equal("https://from-code", fromCode.Url);
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("0", false)]
    [InlineData("off", false)]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("", true)] // boş = verilmemiş
    public void Enabled_flag(string value, bool expected)
    {
        Assert.Equal(expected, Resolve(env: Env(("VITALWATCH_ENABLED", value))).Enabled);
    }

    [Fact]
    public void Environment_enabled_overrides_appsettings_disabled()
    {
        var settings = Resolve(Config(("VitalWatch:Enabled", "false")), Env(("VITALWATCH_ENABLED", "true")));
        Assert.True(settings.Enabled);
    }

    [Fact]
    public void Defaults()
    {
        var settings = Resolve();

        Assert.True(settings.Enabled);
        Assert.False(settings.Configured);
        Assert.Equal(1000, settings.SlowRequestMs);
        Assert.Equal(500, settings.SlowQueryMs);
        Assert.Equal(2000, settings.TimeoutMs);
        Assert.Equal("production", settings.Env);
        Assert.Equal(TimeSpan.FromHours(8), settings.HeartbeatInterval);
    }

    [Theory]
    [InlineData("Development", "local")]
    [InlineData("Production", "production")]
    [InlineData("Staging", "staging")]
    [InlineData("prod", "production")]
    [InlineData("uat", "staging")]
    [InlineData("testing", "local")]
    [InlineData("qa-west", "qa-west")]
    public void Environment_names_follow_shared_alias_table(string hostEnv, string expected)
    {
        Assert.Equal(expected, Resolve(hostEnv: hostEnv).Env);
    }

    [Fact]
    public void Vitalwatch_env_wins_over_aspnetcore_environment()
    {
        Assert.Equal("staging", Resolve(env: Env(("VITALWATCH_ENV", "stg")), hostEnv: "Development").Env);
    }

    [Fact]
    public void Problems_report_incomplete_configuration()
    {
        var settings = Resolve(env: Env(
            ("VITALWATCH_URL", "http://hub.example"),
            ("VITALWATCH_SECRET", "too-short"),
            ("VITALWATCH_ENV", "qa")));

        var problems = string.Join("\n", settings.Problems());

        Assert.Contains("VITALWATCH_KEY not set", problems);
        Assert.Contains("must be 64 characters, got 9", problems);
        Assert.Contains("http://", problems);
        Assert.Contains("\"qa\"", problems);
    }

    [Fact]
    public void Complete_configuration_has_no_problems()
    {
        var settings = Resolve(env: Env(
            ("VITALWATCH_URL", "https://hub.example"),
            ("VITALWATCH_KEY", "k"),
            ("VITALWATCH_SECRET", FakeHub.DefaultSecret)));

        Assert.Empty(settings.Problems());
        Assert.True(settings.Active);
    }

    [Theory]
    [InlineData("SlowRequestMs", "SLOW_REQUEST_MS")]
    [InlineData("SlowQueryMs", "SLOW_QUERY_MS")]
    [InlineData("Url", "URL")]
    public void Environment_variable_names(string property, string expected)
    {
        Assert.Equal(expected, Settings.ToEnvName(property));
    }

    [Fact]
    public void Dependencies_default_on()
    {
        Assert.True(Resolve().Dependencies);
        Assert.Equal("DEPENDENCIES", Settings.ToEnvName(nameof(VitalWatchOptions.Dependencies)));
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("off", false)]
    [InlineData("no", false)]
    [InlineData("true", true)]
    [InlineData("", true)] // boş = verilmemiş
    public void Dependencies_flag_from_environment(string value, bool expected)
    {
        Assert.Equal(expected, Resolve(env: Env(("VITALWATCH_DEPENDENCIES", value))).Dependencies);
    }

    [Fact]
    public void Dependencies_precedence_appsettings_then_environment_then_code()
    {
        Assert.False(Resolve(Config(("VitalWatch:Dependencies", "false"))).Dependencies);

        // Ortam dosyayı yener — iki yönde de.
        Assert.True(Resolve(Config(("VitalWatch:Dependencies", "false")), Env(("VITALWATCH_DEPENDENCIES", "true"))).Dependencies);
        Assert.False(Resolve(Config(("VitalWatch:Dependencies", "true")), Env(("VITALWATCH_DEPENDENCIES", "false"))).Dependencies);

        // Kod her şeyi yener.
        Assert.True(Resolve(
            Config(("VitalWatch:Dependencies", "false")),
            Env(("VITALWATCH_DEPENDENCIES", "false")),
            configure: o => o.Dependencies = true).Dependencies);
    }
}
