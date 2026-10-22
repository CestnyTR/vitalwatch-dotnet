using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace VitalWatch.Tests;

/// <summary>
/// Gerçek EF Core (SQLite) ile uçtan uca: sahte yük değil, EF'nin kendi yazdığı
/// <c>CommandExecuted</c> olayı. Paket EF'ye bağımlı olmadığı için yük adla okunuyor;
/// EF bir gün özelliği yeniden adlandırırsa bu test düşer.
/// </summary>
public sealed class EfCoreTests : IAsyncLifetime
{
    private FakeHub _hub = null!;
    private SqliteConnection _connection = null!;

    public async Task InitializeAsync()
    {
        _hub = await FakeHub.StartAsync();
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
        await _hub.DisposeAsync();
    }

    public sealed class Product
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    public sealed class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
    {
        public DbSet<Product> Products => Set<Product>();
    }

    [Fact]
    public async Task Real_ef_core_queries_are_counted_and_normalized()
    {
        await using var app = await TestApp.StartAsync(_hub, app =>
        {
            app.MapGet("/products/{name}", async (string name, ShopContext db) =>
            {
                await db.Database.EnsureCreatedAsync();
                db.Products.Add(new Product { Name = "kalem" });
                await db.SaveChangesAsync();

                // Parametreli sorgu: değer SQL metnine girmez.
                var found = await db.Products.Where(p => p.Name == name).CountAsync();

                // Literal gömülü ham SQL: normalizasyon değeri atmalı.
                var raw = await db.Database
                    .SqlQueryRaw<int>("SELECT COUNT(*) AS \"Value\" FROM \"Products\" WHERE \"Name\" = 'ahmet@ornek.com' AND \"Id\" > 5")
                    .SingleAsync();

                return (found + raw).ToString();
            });
        }, options: o =>
        {
            o.SlowRequestMs = 0;
            o.SlowQueryMs = 0; // her sorgu "yavaş": yük adlarının okunduğu kanıtlanır
        }, services: s => s.AddDbContext<ShopContext>(o => o.UseSqlite(_connection)));

        var response = await app.Client().GetAsync("/products/kalem");
        response.EnsureSuccessStatusCode();

        Assert.True(await _hub.WaitForAsync(h => h.EventsOf("slow_request").Count == 1));

        var request = _hub.EventsOf("slow_request")[0];
        Assert.True(request.GetProperty("query_count").GetInt32() >= 3, request.ToString());

        var queries = _hub.EventsOf("slow_query").Select(e => e.GetProperty("msg").GetString()!).ToArray();
        Assert.NotEmpty(queries);
        Assert.All(_hub.EventsOf("slow_query"), e => Assert.Equal("GET /products/{name}", e.GetProperty("route").GetString()));
        Assert.Contains(queries, q => q.Contains("WHERE ? = ? AND ? > ?"));
        Assert.DoesNotContain("ahmet", string.Join("\n", _hub.Requests.Select(r => r.Raw)));
    }
}
