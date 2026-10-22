using System.Text.Encodings.Web;
using System.Text.Json;

namespace VitalWatch.Internal;

/// <summary>
/// Olayları hub'ın kabul ettiği gövdeye çevirir: istek başına en çok 8 olay ve 8192 bayt.
/// </summary>
/// <remarks>
/// İki sınır da hub tarafında <b>sessiz</b> eleme (vitalwatch-hub <c>IngestController</c>):
/// 8 KB'ı aşan gövde okunmadan atılır, 8'den fazla olayın fazlası kesilir — ikisi de yine
/// 204 döner. Bu yüzden burada uyulur.
///
/// Sığmayan tek olay Python paketindeki sırayla küçültülür: önce stack atılır, sonra mesaj
/// 200 karaktere kırpılır. Olayı hiç göndermemektense stack'siz göndermek yeğdir. Birden çok
/// olay sığmıyorsa sayı azaltılır; kalanlar sonraki isteğe kalır — ayrıntı kaybetmek yerine
/// bir istek daha atmak yeğdir.
/// </remarks>
internal static class PayloadEncoder
{
    public const int MaxBodyBytes = 8192;
    public const int MaxEvents = 8;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // Türkçe karakterler \uXXXX yerine UTF-8 kalır: aynı metin 8 KB'a daha çok sığar.
        // Gövde HTML'e gömülmüyor; gevşek kaçış güvenli.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Kuyruğun başından sığdığı kadar olayı kodlar.
    /// </summary>
    /// <returns>
    /// Gövde ve tüketilen olay sayısı. Tek olay hiçbir küçültmeyle sığmazsa gövde
    /// <c>null</c>, tüketilen 1'dir — olay atılır, kuyruk tıkanmaz.
    /// </returns>
    public static (byte[]? Body, int Consumed) Encode(IReadOnlyList<HubEvent> pending, Settings settings)
    {
        if (pending.Count == 0)
        {
            return (null, 0);
        }

        var count = Math.Min(MaxEvents, pending.Count);

        while (count > 1)
        {
            var body = Write(pending, count, settings, Trim.None);
            if (body.Length <= MaxBodyBytes)
            {
                return (body, count);
            }

            count--;
        }

        foreach (var trim in new[] { Trim.None, Trim.Stack, Trim.StackAndMessage })
        {
            var body = Write(pending, 1, settings, trim);
            if (body.Length <= MaxBodyBytes)
            {
                return (body, 1);
            }
        }

        return (null, 1);
    }

    /// <summary>Canlılık isteği: <c>{"events":[]}</c> + kimlik.</summary>
    public static byte[] Heartbeat(Settings settings) => Write([], 0, settings, Trim.None);

    private enum Trim
    {
        None,
        Stack,
        StackAndMessage,
    }

    private static byte[] Write(IReadOnlyList<HubEvent> events, int count, Settings settings, Trim trim)
    {
        using var stream = new MemoryStream(1024);
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("events");

            for (var i = 0; i < count; i++)
            {
                writer.WriteStartObject();

                foreach (var (key, value) in events[i].Fields)
                {
                    if (value is null)
                    {
                        continue;
                    }

                    if (trim != Trim.None && key == "stack")
                    {
                        continue;
                    }

                    if (trim == Trim.StackAndMessage && key == "msg" && value is string msg)
                    {
                        writer.WriteString(key, Scrubber.Truncate(msg, 200));
                        continue;
                    }

                    WriteValue(writer, key, value);
                }

                /*
                 * Kimlik olayın içinde de: hub `release`'i ve bağlam alanlarını
                 * (`runtime`, `runtime_version`, `sdk_version`) olay başına okuyor;
                 * üst seviyedekini yalnızca canlılık damgası için kullanıyor.
                 */
                WriteIdentity(writer, settings, includeEnv: false);

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            WriteIdentity(writer, settings, includeEnv: true);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void WriteIdentity(Utf8JsonWriter writer, Settings settings, bool includeEnv)
    {
        if (includeEnv)
        {
            writer.WriteString("env", settings.Env);
            writer.WriteString("source", "server");
        }

        if (settings.Release is not null)
        {
            writer.WriteString("release", settings.Release);
        }

        writer.WriteString("runtime", SdkInfo.Runtime);
        writer.WriteString("runtime_version", SdkInfo.RuntimeVersion);
        writer.WriteString("sdk_version", SdkInfo.Version);
    }

    private static void WriteValue(Utf8JsonWriter writer, string key, object value)
    {
        switch (value)
        {
            case string s:
                writer.WriteString(key, s);
                break;
            case int i:
                writer.WriteNumber(key, i);
                break;
            case long l:
                writer.WriteNumber(key, l);
                break;
            case double d:
                writer.WriteNumber(key, (long)Math.Round(d));
                break;
            case bool b:
                writer.WriteBoolean(key, b);
                break;
            default:
                writer.WriteString(key, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                break;
        }
    }
}
