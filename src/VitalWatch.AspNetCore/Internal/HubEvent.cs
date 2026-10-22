namespace VitalWatch.Internal;

/// <summary>
/// Tek olay — alan adı ve değer çiftleri, ekleme sırasıyla.
/// </summary>
/// <remarks>
/// Değerler yalnızca <c>string</c>, <c>int</c>, <c>long</c>, <c>double</c>, <c>bool</c>;
/// <c>null</c> alan gönderilmez. Kimlik alanları (<c>release</c>, <c>runtime</c> …) burada
/// tutulmaz, kodlayıcı ekler.
/// </remarks>
internal sealed class HubEvent
{
    /// <summary>Hub'ın kabul ettiği sunucu türleri (vitalwatch-hub, <c>IngestService::KINDS_SERVER</c>).</summary>
    public static readonly string[] Kinds =
        ["exception", "fatal", "http_5xx", "slow_request", "slow_query", "job_failed", "command_failed"];

    public List<KeyValuePair<string, object?>> Fields { get; } = new(12);

    public HubEvent(string kind, string msg)
    {
        Set("kind", kind);
        Set("msg", msg);
    }

    public string Kind => (string)Fields[0].Value!;

    public HubEvent Set(string key, object? value)
    {
        for (var i = 0; i < Fields.Count; i++)
        {
            if (Fields[i].Key == key)
            {
                Fields[i] = new(key, value);

                return this;
            }
        }

        Fields.Add(new(key, value));

        return this;
    }

    public object? Get(string key)
    {
        foreach (var (k, v) in Fields)
        {
            if (k == key)
            {
                return v;
            }
        }

        return null;
    }
}
