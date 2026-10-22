using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using VitalWatch.Internal;

namespace VitalWatch.Tests;

/// <summary>
/// SQL ölçümü: EF Core, SqlClient ve Npgsql'in tanılama yükleri sahte kaynaklarla üretilir.
/// Yük biçimleri gerçek kütüphanelerdeki özellik adlarıyla aynı.
/// </summary>
public sealed class QueryTests : IAsyncLifetime
{
    private static readonly DiagnosticListener Ef = new(DiagnosticsBridge.EfListener);
    private static readonly DiagnosticListener SqlClient = new(DiagnosticsBridge.SqlClientListener);
    private static readonly ActivitySource Npgsql = new(DiagnosticsBridge.NpgsqlSource);

    private FakeHub _hub = null!;

    public async Task InitializeAsync() => _hub = await FakeHub.StartAsync();

    public async Task DisposeAsync() => await _hub.DisposeAsync();

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    /// <summary>EF Core'un <c>CommandExecutedEventData</c>'sıyla aynı özellik adları.</summary>
    public sealed class FakeCommandEventData(DbCommand command, TimeSpan duration)
    {
        public DbCommand Command { get; } = command;

        public TimeSpan Duration { get; } = duration;
    }

    internal static void EfQuery(string sql, double ms, DbCommand? command = null)
    {
        command ??= new FakeCommand(sql);
        Ef.Write(DiagnosticsBridge.EfCommandExecuting, new FakeCommandEventData(command, TimeSpan.Zero));
        Ef.Write(DiagnosticsBridge.EfCommandExecuted, new FakeCommandEventData(command, TimeSpan.FromMilliseconds(ms)));
    }

    private static void SqlClientQuery(DbCommand command, double ms)
    {
        var operation = Guid.NewGuid();
        var end = Stopwatch.GetTimestamp();
        var start = end - (long)(ms / 1000 * Stopwatch.Frequency);

        // Microsoft.Data.SqlClient'ın yükündeki adlar.
        SqlClient.Write("Microsoft.Data.SqlClient.WriteCommandBefore", new { OperationId = operation, Operation = "ExecuteReader", Command = command, Timestamp = start });
        SqlClient.Write("Microsoft.Data.SqlClient.WriteCommandAfter", new { OperationId = operation, Operation = "ExecuteReader", Command = command, Timestamp = end });
    }

    [Fact]
    public async Task Slow_ef_query_is_reported_normalized_with_route()
    {
        await using var app = await TestApp.StartAsync(_hub, app =>
        {
            app.MapGet("/users/{id}", (string id) =>
            {
                EfQuery("SELECT * FROM users WHERE email = 'ahmet@ornek.com' AND id = 42", 800);
                return "ok";
            });
        });

        await app.Client().GetAsync("/users/42");

        Assert.True(await _hub.WaitForAsync(h => h.EventsOf("slow_query").Count == 1));

        var e = _hub.EventsOf("slow_query")[0];
        Assert.Equal("SELECT * FROM users WHERE email = ? AND id = ?", Str(e, "msg"));
        Assert.Equal("SELECT * FROM users WHERE email = ? AND id = ?", Str(e, "slowest_query_sql"));
        Assert.Equal(800, e.GetProperty("slowest_query_ms").GetInt32());
        Assert.Equal("GET /users/{id}", Str(e, "route"));
        Assert.DoesNotContain("ahmet", string.Join("", _hub.Requests.Select(r => r.Raw)));
    }

    [Fact]
    public async Task Fast_query_is_counted_but_not_reported()
    {
        await using var app = await TestApp.StartAsync(_hub, app =>
        {
            app.MapGet("/list", async () =>
            {
                EfQuery("select 1", 3);
                EfQuery("select * from orders where status = 'open'", 40);
                EfQuery("select 2", 5);
                await Task.Delay(1);
                return "ok";
            });
        }, options: o => o.SlowRequestMs = 0);

        await app.Client().GetAsync("/list");

        Assert.True(await _hub.WaitForAsync(h => h.EventsOf("slow_request").Count == 1));

        var e = _hub.EventsOf("slow_request")[0];
        Assert.Equal(3, e.GetProperty("query_count").GetInt32());
        Assert.Equal(40, e.GetProperty("slowest_query_ms").GetInt32());
        Assert.Equal("select * from orders where status = ?", Str(e, "slowest_query_sql"));
        Assert.Empty(_hub.EventsOf("slow_query"));
    }

    [Fact]
    public async Task Query_count_is_isolated_between_concurrent_requests()
    {
        await using var app = await TestApp.StartAsync(_hub, app =>
        {
            app.MapGet("/q/{n:int}", async (int n) =>
            {
                for (var i = 0; i < n; i++)
                {
                    EfQuery($"select {i}", 1);

                    // Await zincirinde başka thread'e geçilse de kapsam izler.
                    await Task.Yield();
                    await Task.Delay(Random.Shared.Next(1, 5));
                }

                return n.ToString();
            });
        }, options: o => o.SlowRequestMs = 0);

        var client = app.Client();
        var counts = Enumerable.Range(1, 20).ToArray();

        await Task.WhenAll(counts.Select(n => client.GetAsync($"/q/{n}")));

        Assert.True(await _hub.WaitForAsync(h => h.EventsOf("slow_request").Count == 20, 10000));

        var reported = _hub.EventsOf("slow_request").Select(e => e.GetProperty("query_count").GetInt32()).OrderBy(x => x);
        Assert.Equal(counts, reported);
    }

    [Fact]
    public async Task Query_outside_request_reports_slow_query_without_route()
    {
        await using var app = await TestApp.StartAsync(_hub, _ => { });

        await Task.Run(() => EfQuery("delete from jobs where id = 5", 900));

        Assert.True(await _hub.WaitForAsync(h => h.EventsOf("slow_query").Count == 1));

        var e = _hub.EventsOf("slow_query")[0];
        Assert.Equal("delete from jobs where id = ?", Str(e, "msg"));
        Assert.False(e.TryGetProperty("route", out _));
    }

    [Fact]
    public async Task SqlClient_query_is_measured_from_timestamps()
    {
        await using var app = await TestApp.StartAsync(_hub, app =>
        {
            app.MapGet("/dapper", () =>
            {
                SqlClientQuery(new FakeCommand("select * from t where name = N'ayşe'"), 700);
                return "ok";
            });
        });

        await app.Client().GetAsync("/dapper");

        Assert.True(await _hub.WaitForAsync(h => h.EventsOf("slow_query").Count == 1));

        var e = _hub.EventsOf("slow_query")[0];
        Assert.InRange(e.GetProperty("slowest_query_ms").GetInt32(), 690, 710);
        Assert.Equal("select * from t where name = N?", Str(e, "msg"));
    }

    [Fact]
    public async Task Ef_and_sqlclient_do_not_double_count_the_same_command()
    {
        await using var app = await TestApp.StartAsync(_hub, app =>
        {
            app.MapGet("/both", () =>
            {
                var command = new FakeCommand("select * from products");

                // Gerçek sıra: EF Executing → SqlClient Before/After → EF Executed.
                Ef.Write(DiagnosticsBridge.EfCommandExecuting, new FakeCommandEventData(command, TimeSpan.Zero));
                SqlClientQuery(command, 20);
                Ef.Write(DiagnosticsBridge.EfCommandExecuted, new FakeCommandEventData(command, TimeSpan.FromMilliseconds(21)));

                // EF dışı ham komut ayrıca sayılır.
                SqlClientQuery(new FakeCommand("select 1"), 2);
                return "ok";
            });
        }, options: o => o.SlowRequestMs = 0);

        await app.Client().GetAsync("/both");

        Assert.True(await _hub.WaitForAsync(h => h.EventsOf("slow_request").Count == 1));
        Assert.Equal(2, _hub.EventsOf("slow_request")[0].GetProperty("query_count").GetInt32());
    }

    [Fact]
    public async Task Npgsql_activity_is_measured()
    {
        await using var app = await TestApp.StartAsync(_hub, app =>
        {
            app.MapGet("/pg", () =>
            {
                using (var activity = Npgsql.StartActivity(
                           "users",
                           ActivityKind.Client,
                           default(ActivityContext),
                           [new("db.statement", "SELECT * FROM users WHERE id = 7")],
                           startTime: DateTimeOffset.UtcNow.AddMilliseconds(-900)))
                {
                    Assert.NotNull(activity);
                }

                return "ok";
            });
        });

        await app.Client().GetAsync("/pg");

        Assert.True(await _hub.WaitForAsync(h => h.EventsOf("slow_query").Count == 1));

        var e = _hub.EventsOf("slow_query")[0];
        Assert.Equal("SELECT * FROM users WHERE id = ?", Str(e, "msg"));
        Assert.InRange(e.GetProperty("slowest_query_ms").GetInt32(), 880, 2000);
    }

    [Fact]
    public async Task Npgsql_activity_is_skipped_when_ef_reports_the_request()
    {
        await using var app = await TestApp.StartAsync(_hub, app =>
        {
            app.MapGet("/pg-ef", () =>
            {
                var command = new FakeCommand("select * from users");
                Ef.Write(DiagnosticsBridge.EfCommandExecuting, new FakeCommandEventData(command, TimeSpan.Zero));
                Npgsql.StartActivity("users", ActivityKind.Client, default(ActivityContext), [new("db.statement", "select * from users")])?.Stop();
                Ef.Write(DiagnosticsBridge.EfCommandExecuted, new FakeCommandEventData(command, TimeSpan.FromMilliseconds(4)));
                return "ok";
            });
        }, options: o => o.SlowRequestMs = 0);

        await app.Client().GetAsync("/pg-ef");

        Assert.True(await _hub.WaitForAsync(h => h.EventsOf("slow_request").Count == 1));
        Assert.Equal(1, _hub.EventsOf("slow_request")[0].GetProperty("query_count").GetInt32());
    }

    [Fact]
    public async Task Exception_event_carries_query_context()
    {
        await using var app = await TestApp.StartAsync(_hub, app =>
        {
            app.MapGet("/crash", () =>
            {
                EfQuery("select * from carts where user_id = 3", 12);
                return MiddlewareTests.Throw("sepet bozuk");
            });
        }, services: s => Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions
            .AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(s, new MiddlewareTests.OuterCatch(_ => { })));

        await app.Client().GetAsync("/crash");

        Assert.True(await _hub.WaitForAsync(h => h.EventsOf("exception").Count == 1));

        var e = _hub.EventsOf("exception")[0];
        Assert.Equal(1, e.GetProperty("query_count").GetInt32());
        Assert.Equal("select * from carts where user_id = ?", Str(e, "slowest_query_sql"));
    }

    /// <summary>Yalnızca <c>CommandText</c> taşıyan sahte komut.</summary>
    internal sealed class FakeCommand(string text) : DbCommand
    {
        [AllowNull]
        public override string CommandText { get; set; } = text;

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; }

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();

        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel()
        {
        }

        public override int ExecuteNonQuery() => 0;

        public override object? ExecuteScalar() => null;

        public override void Prepare()
        {
        }

        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
    }
}
