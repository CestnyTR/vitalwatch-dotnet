namespace VitalWatch.Internal;

/// <summary>
/// Ortam adı normalleştirme — Laravel, Node ve Python paketleriyle aynı tablo.
/// </summary>
/// <remarks>
/// Hub yalnızca production, staging ve local kabul ediyor; küme dışındaki olayı — canlılık
/// dahil — 204 dönüp atıyor. ASP.NET Core'un varsayılan adları (<c>Development</c>,
/// <c>Staging</c>, <c>Production</c>) küçük harfe indirilip aynı tablodan geçer:
/// <c>Development</c> → <c>local</c>. Tanınmayan değer olduğu gibi gider (hub onu panelde
/// "ortam" reddi olarak sayar) ve teşhis komutu hata verir. Bilinmeyeni production saymak
/// test verisini canlı verinin arasına karıştırırdı.
/// </remarks>
internal static class EnvironmentNames
{
    public static readonly string[] Accepted = ["production", "staging", "local"];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["production"] = "production",
        ["prod"] = "production",
        ["live"] = "production",
        ["staging"] = "staging",
        ["stage"] = "staging",
        ["stg"] = "staging",
        ["preprod"] = "staging",
        ["uat"] = "staging",
        ["local"] = "local",
        ["dev"] = "local",
        ["development"] = "local",
        ["test"] = "local",
        ["testing"] = "local",
    };

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "production";
        }

        var trimmed = value.Trim();

        return Aliases.TryGetValue(trimmed.ToLowerInvariant(), out var name) ? name : trimmed;
    }

    public static bool IsAccepted(string value) => Array.IndexOf(Accepted, value) >= 0;
}
