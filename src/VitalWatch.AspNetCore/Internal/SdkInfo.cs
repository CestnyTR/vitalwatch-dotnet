using System.Reflection;

namespace VitalWatch.Internal;

/// <summary>
/// SDK kimliği — her isteğin üst seviyesine eklenir.
/// </summary>
/// <remarks>
/// Hub kimliği olay içinden değil gövdenin üst seviyesinden okuyor: canlılık isteği hiç olay
/// taşımıyor ve kurulumun hangi paketle çalıştığı en çok orada — hiç hata üretmeyen projede —
/// lazım. <c>sdk_version</c> olmadan hangi projenin eski sürümde kaldığı görülemez.
/// </remarks>
internal static class SdkInfo
{
    public const string Runtime = "dotnet";

    public static readonly string Version = ReadVersion();

    /// <summary>Örn. <c>10.0.12</c>.</summary>
    public static readonly string RuntimeVersion = Environment.Version.ToString();

    private static string ReadVersion()
    {
        try
        {
            var informational = typeof(SdkInfo).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                // "0.1.0+abc123" → "0.1.0": hub alanı 32 karakterle sınırlı.
                var plus = informational.IndexOf('+');

                return plus >= 0 ? informational[..plus] : informational;
            }

            return typeof(SdkInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }
        catch
        {
            return "0.0.0";
        }
    }
}
