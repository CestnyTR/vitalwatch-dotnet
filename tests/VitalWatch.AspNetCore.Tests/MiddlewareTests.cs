using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using VitalWatch.Internal;

namespace VitalWatch.Tests;

public sealed class MiddlewareTests : IAsyncLifetime
{
    private FakeHub _hub = null!;

    public async Task InitializeAsync() => _hub = await FakeHub.StartAsync();

    public async Task DisposeAsync() => await _hub.DisposeAsync();

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    [Fact]
    public async Task Exception_is_reported_with_route_pattern_and_rethrown()
    {
        Exception? seenOutside = null;

        await using var app = await TestApp.StartAsync(_hub, app =>
        {
            app.MapGet("/api/products/{id}", (string id) => Throw("boom for ahmet@ornek.com"));
        }, services: s => s.AddSingleton<IStartupFilter>(new OuterCatch(e => seenOutside = e)));

        var response = await app.Client().GetAsync("/api/products/42");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        // Yeniden fırlatıldı: dıştaki katman aynı istisnayı, özgün stack'iyle gördü.
        Assert.IsType<InvalidOperationException>(seenOutside);
        Assert.Contains("MiddlewareTests", seenOutside!.StackTrace);

        Assert.True(await _hub.WaitForAsync(h => h.EventsOf("exception").Count == 1));

        var e = _hub.EventsOf("exception")[0];
        Assert.Equal("GET /api/products/{id}", Str(e, "route"));
        Assert.Equal("GET", Str(e, "method"));
        Assert.Equal("boom for [email]", Str(e, "msg"));
        Assert.Equal("System.InvalidOperationException", Str(e, "exception_class"));
        Assert.DoesNotContain("ahmet@ornek.com", Str(e, "stack"));
        Assert.Contains("boom for [email]", Str(e, "stack"));
        Assert.Contains("MiddlewareTests.Throw", Str(e, "stack"));

        // pdb var: dosya ve satır bilinir.
        Assert.EndsWith("MiddlewareTests.cs", Str(e, "file"));
        Assert.True(e.GetProperty("line").GetInt32() > 0);

        // Aynı arıza için ayrıca "HTTP 500" yok.
        await Task.Delay(200);
        Assert.Empty(_hub.EventsOf("http_5xx"));
        Assert.All(_hub.Requests, r => Assert.True(r.Accepted));
    }

    [Fact]
    public async Task Server_error_without_exception_is_http_5xx()
    {
        await using var app = await TestApp.StartAsync(_hub, app =>
        {
            app.MapGet("/health/{name}", (string name) => Results.StatusCode(503));
        });

        await app.Client().GetAsync("/health/db");

        Assert.True(await _hub.WaitForAsync(h => h.EventsOf("http_5xx").Count == 1));

        var e = _hub.EventsOf("http_5xx")[0];
        Assert.Equal("GET /health/{name} — HTTP 503", Str(e, "msg"));
        Assert.Equal("GET /health/{name}", Str(e, "route"));
        Assert.Equal(503, e.GetProperty("status").GetInt32());
        Assert.True(e.TryGetProperty("duration_ms", out _));
        Assert.True(e.GetProperty("memory_mb").GetInt32() > 0);
    }

    [Fact]
    public async Task Slow_request_is_reported_fast_one_is_not()
    {
        await using var app = await TestApp.StartAsync(_hub, app =>
        {
            app.MapGet("/slow/{id:int}", async (int id) =>
            {
                await Task.Delay(150);
                return "ok";
            });
            app.MapGet("/fast", () => "ok");
        }, options: o => o.SlowRequestMs = 100);

        await app.Client().GetAsync("/fast");
        await app.Client().GetAsync("/slow/7");

        Assert.True(await _hub.WaitForAsync(h => h.EventsOf("slow_request").Count == 1));
        await Task.Delay(150);

        var events = _hub.EventsOf("slow_request");
        Assert.Single(events);

        var e = events[0];
        Assert.Equal("GET /slow/{id:int}", Str(e, "route"));
        Assert.Equal(200, e.GetProperty("status").GetInt32());
        Assert.True(e.GetProperty("duration_ms").GetInt64() >= 140);
        Assert.StartsWith("GET /slow/{id:int} — ", Str(e, "msg"));
        Assert.EndsWith(" ms", Str(e, "msg"));
    }

    [Fact]
    public async Task Exception_handler_after_vitalwatch_is_seen_via_diagnostics()
    {
        // Sıra: UseVitalWatch → UseExceptionHandler → uç. Ara katman istisnayı hiç görmez;
        // işleyicinin tanılama olayından alınır.
        await using var app = await TestApp.StartAsync(_hub, app =>
        {
            app.UseExceptionHandler(error => error.Run(ctx =>
            {
                ctx.Response.StatusCode = 500;
                return ctx.Response.WriteAsync("hata");
            }));

            app.MapGet("/orders/{id}", (string id) => Throw("sipariş patladı"));
        });

        var response = await app.Client().GetAsync("/orders/5");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        Assert.True(await _hub.WaitForAsync(h => h.EventsOf("exception").Count == 1));
        await Task.Delay(200);

        var e = Assert.Single(_hub.EventsOf("exception"));
        Assert.Equal("GET /orders/{id}", Str(e, "route"));
        Assert.Equal("sipariş patladı", Str(e, "msg"));
        Assert.Empty(_hub.EventsOf("http_5xx"));
    }

    [Fact]
    public async Task Exception_handler_path_before_vitalwatch_reports_original_route_once()
    {
        // Sıra: UseExceptionHandler("/error") → UseVitalWatch → uç. İşleyici isteği "/error"
        // yoluna yeniden yürütür; ikinci geçiş ölçülmez.
        await using var app = await TestApp.StartAsync(_hub, app =>
        {
            app.UseExceptionHandler("/error");
            app.UseVitalWatch();
            app.MapGet("/error", () => Results.Text("hata sayfası", statusCode: 500));
            app.MapGet("/orders/{id}", (string id) => Throw("ikinci sıra"));
        }, useVitalWatch: false);

        var response = await app.Client().GetAsync("/orders/9");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        Assert.True(await _hub.WaitForAsync(h => h.EventsOf("exception").Count == 1));
        await Task.Delay(200);

        var e = Assert.Single(_hub.EventsOf("exception"));
        Assert.Equal("GET /orders/{id}", Str(e, "route"));
        Assert.Empty(_hub.EventsOf("http_5xx"));
    }

    [Fact]
    public async Task Exception_mapped_to_4xx_by_handler_is_not_reported()
    {
        await using var app = await TestApp.StartAsync(_hub, app =>
        {
            app.UseExceptionHandler();
            app.MapGet("/validate", () => Throw<ArgumentException>("geçersiz girdi"));
        }, services: s =>
        {
            s.AddProblemDetails();
            s.AddExceptionHandler<BadRequestHandler>();
        });

        var response = await app.Client().GetAsync("/validate");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await Task.Delay(400);
        Assert.Empty(_hub.Events);
    }

    [Fact]
    public async Task Bad_request_exception_is_not_reported()
    {
        await using var app = await TestApp.StartAsync(_hub, app =>
        {
            app.MapGet("/bad", () => Throw<BadHttpRequestException>("bozuk istek"));
        }, services: s => s.AddSingleton<IStartupFilter>(new OuterCatch(_ => { })));

        await app.Client().GetAsync("/bad");

        await Task.Delay(400);
        Assert.Empty(_hub.EventsOf("exception"));
    }

    [Fact]
    public async Task Ip_user_agent_query_cookies_and_body_are_never_sent()
    {
        await using var app = await TestApp.StartAsync(_hub, app =>
        {
            app.MapPost("/api/users/{id}", (string id) => Throw("kayıt hatası"));
            app.MapGet("/api/users/{id}", (string id) => Results.StatusCode(500));
        }, services: s => s.AddSingleton<IStartupFilter>(new OuterCatch(_ => { })));

        var client = app.Client();

        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post })
        {
            using var request = new HttpRequestMessage(method, "/api/users/12345?email=ahmet@ornek.com&q=querysecretvalue");
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 UniqueTestAgent");
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.77");
            request.Headers.TryAddWithoutValidation("Cookie", "session=cookiesecretvalue");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer headersecretvalue");

            if (method == HttpMethod.Post)
            {
                request.Content = new StringContent("{\"password\":\"bodysecretvalue\"}", System.Text.Encoding.UTF8, "application/json");
            }

            await client.SendAsync(request);
        }

        Assert.True(await _hub.WaitForAsync(h => h.EventsOf("exception").Count == 1 && h.EventsOf("http_5xx").Count == 1));

        var everything = string.Join("\n", _hub.Requests.Select(r => r.Raw));

        foreach (var secret in new[]
                 {
                     "ahmet", "querysecretvalue", "UniqueTestAgent", "Mozilla", "203.0.113.77", "127.0.0.1",
                     "cookiesecretvalue", "headersecretvalue", "bodysecretvalue", "12345", "email=",
                 })
        {
            Assert.DoesNotContain(secret, everything);
        }

        Assert.Contains("/api/users/{id}", everything);
    }

    [Fact]
    public void Conventional_mvc_route_uses_controller_and_action_names()
    {
        var context = new DefaultHttpContext();
        context.Request.RouteValues["controller"] = "Orders";
        context.Request.RouteValues["action"] = "Details";

        Assert.Equal("/Orders/Details/{id?}", RouteDescriber.Normalize("{controller=Home}/{action=Index}/{id?}", context, null));
        Assert.Equal("/api/products/{id}", RouteDescriber.Normalize("api/products/{id}", context, null));
    }

    [Fact]
    public void Unmatched_request_falls_back_to_scrubbed_path()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/kullanici/ahmet@ornek.com/profil";
        context.Request.QueryString = new QueryString("?token=x");

        Assert.Equal("GET /kullanici/[email]/profil", RouteDescriber.Describe(context));
    }

    [Fact]
    public void Route_endpoint_pattern_is_used()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "DELETE";
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse("api/items/{id}"), 0, null, null));

        Assert.Equal("DELETE /api/items/{id}", RouteDescriber.Describe(context));
    }

    internal static string Throw(string message) => throw new InvalidOperationException(message);

    internal static string Throw<T>(string message)
        where T : Exception =>
        throw (T)Activator.CreateInstance(typeof(T), message)!;

    /// <summary>Uygulamanın en dışında istisnayı yakalayan katman — yeniden fırlatıldığını kanıtlar.</summary>
    internal sealed class OuterCatch(Action<Exception> seen) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, inner) =>
            {
                try
                {
                    await inner(context);
                }
                catch (Exception e)
                {
                    seen(e);
                    context.Response.StatusCode = 500;
                }
            });

            next(app);
        };
    }

    private sealed class BadRequestHandler : IExceptionHandler
    {
        public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
        {
            if (exception is not ArgumentException)
            {
                return false;
            }

            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("geçersiz", cancellationToken);

            return true;
        }
    }
}
