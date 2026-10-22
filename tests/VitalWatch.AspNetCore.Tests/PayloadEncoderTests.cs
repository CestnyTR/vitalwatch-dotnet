using System.Text.Json;
using VitalWatch.Internal;

namespace VitalWatch.Tests;

public class PayloadEncoderTests
{
    private static readonly Settings Settings = new()
    {
        Url = "https://hub.example",
        Key = "k",
        Secret = FakeHub.DefaultSecret,
        Release = "abc123def456",
    };

    private static HubEvent Small(int i) => new HubEvent("exception", $"hata {i}").Set("route", "GET /x");

    /// <summary>Kuyruğu gönderici gibi boşaltır; her gövdeyi döndürür.</summary>
    private static List<byte[]> Drain(List<HubEvent> pending)
    {
        var bodies = new List<byte[]>();

        while (pending.Count > 0)
        {
            var (body, consumed) = PayloadEncoder.Encode(pending, Settings);
            Assert.True(consumed > 0);
            pending.RemoveRange(0, consumed);

            if (body is not null)
            {
                bodies.Add(body);
            }
        }

        return bodies;
    }

    private static int EventCount(byte[] body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("events").GetArrayLength();

    [Fact]
    public void At_most_eight_events_per_request()
    {
        var bodies = Drain(Enumerable.Range(0, 20).Select(Small).ToList());

        Assert.Equal([8, 8, 4], bodies.Select(EventCount));
        Assert.All(bodies, b => Assert.True(b.Length <= PayloadEncoder.MaxBodyBytes));
    }

    [Fact]
    public void Large_events_are_split_to_fit_8_kb_without_losing_stacks()
    {
        var events = Enumerable.Range(0, 8)
            .Select(i => Small(i).Set("stack", new string('s', 2000)).Set("msg", new string('m', 500)))
            .ToList();

        var bodies = Drain(events);

        Assert.Equal(8, bodies.Sum(EventCount));
        Assert.True(bodies.Count > 1);
        Assert.All(bodies, b => Assert.True(b.Length <= PayloadEncoder.MaxBodyBytes));
        Assert.All(bodies.SelectMany(b => JsonDocument.Parse(b).RootElement.GetProperty("events").EnumerateArray()),
            e => Assert.Equal(2000, e.GetProperty("stack").GetString()!.Length));
    }

    [Fact]
    public void Oversized_single_event_drops_stack_first()
    {
        // 3 baytlık karakterler: 2000 × 3 + 500 × 3 + 300 × 3 > 8192.
        var e = new HubEvent("exception", new string('中', 500))
            .Set("stack", new string('中', 2000))
            .Set("route", "GET /" + new string('中', 300));

        var (body, consumed) = PayloadEncoder.Encode([e], Settings);

        Assert.Equal(1, consumed);
        Assert.NotNull(body);
        Assert.True(body!.Length <= PayloadEncoder.MaxBodyBytes);

        var encoded = JsonDocument.Parse(body).RootElement.GetProperty("events")[0];
        Assert.False(encoded.TryGetProperty("stack", out _));
        Assert.Equal(500, encoded.GetProperty("msg").GetString()!.Length);
    }

    [Fact]
    public void Then_trims_message_to_200()
    {
        var e = new HubEvent("slow_query", new string('中', 2000))
            .Set("slowest_query_sql", new string('中', 500))
            .Set("route", "GET /" + new string('中', 300));

        var (body, _) = PayloadEncoder.Encode([e], Settings);

        Assert.NotNull(body);
        Assert.Equal(200, JsonDocument.Parse(body!).RootElement.GetProperty("events")[0].GetProperty("msg").GetString()!.Length);
    }

    [Fact]
    public void Unfittable_event_is_dropped_and_queue_moves_on()
    {
        var huge = new HubEvent("exception", "x").Set("route", new string('r', 9000));
        var pending = new List<HubEvent> { huge, Small(1) };

        var (body, consumed) = PayloadEncoder.Encode(pending, Settings);

        Assert.Null(body);
        Assert.Equal(1, consumed);
    }

    [Fact]
    public void Identity_is_top_level_and_per_event()
    {
        var (body, _) = PayloadEncoder.Encode([Small(1)], Settings);
        var root = JsonDocument.Parse(body!).RootElement;

        Assert.Equal("dotnet", root.GetProperty("runtime").GetString());
        Assert.Equal(Environment.Version.ToString(), root.GetProperty("runtime_version").GetString());
        Assert.Equal("0.1.1", root.GetProperty("sdk_version").GetString());
        Assert.Equal("production", root.GetProperty("env").GetString());
        Assert.Equal("server", root.GetProperty("source").GetString());
        Assert.Equal("abc123def456", root.GetProperty("release").GetString());

        // Hub release'i ve bağlam alanlarını olay başına okuyor.
        var e = root.GetProperty("events")[0];
        Assert.Equal("abc123def456", e.GetProperty("release").GetString());
        Assert.Equal("dotnet", e.GetProperty("runtime").GetString());
        Assert.Equal("0.1.1", e.GetProperty("sdk_version").GetString());
    }

    [Fact]
    public void Heartbeat_is_empty_event_list_with_identity()
    {
        var root = JsonDocument.Parse(PayloadEncoder.Heartbeat(Settings)).RootElement;

        Assert.Equal(0, root.GetProperty("events").GetArrayLength());
        Assert.Equal("dotnet", root.GetProperty("runtime").GetString());
    }

    [Fact]
    public void Non_ascii_text_is_not_escaped()
    {
        var (body, _) = PayloadEncoder.Encode([new HubEvent("exception", "Sipariş bulunamadı")], Settings);

        Assert.Contains("Sipariş bulunamadı", System.Text.Encoding.UTF8.GetString(body!));
    }
}
