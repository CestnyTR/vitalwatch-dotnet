namespace VitalWatch;

/// <summary>
/// VitalWatch yapılandırması.
/// </summary>
/// <remarks>
/// Değerler şu sırayla okunur, sonraki öncekini ezer:
/// <list type="number">
/// <item><c>appsettings.json</c> içindeki <c>VitalWatch</c> bölümü</item>
/// <item>Süreç ortamındaki <c>VITALWATCH_*</c> değişkenleri — kardeş paketlerdeki kural:
/// süreç ortamı dosyayı her zaman yener</item>
/// <item><c>AddVitalWatch(options =&gt; ...)</c> içinde koddan verilen değerler</item>
/// </list>
/// </remarks>
public sealed class VitalWatchOptions
{
    /// <summary>
    /// <c>false</c> ise hiçbir kanca kurulmaz ve hiçbir veri gönderilmez
    /// (<c>VITALWATCH_ENABLED</c>). Varsayılan <c>true</c>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Hub adresi, örn. <c>https://hub.ornek.com</c> (<c>VITALWATCH_URL</c>).</summary>
    public string? Url { get; set; }

    /// <summary>Panelden alınan proje anahtarı (<c>VITALWATCH_KEY</c>).</summary>
    public string? Key { get; set; }

    /// <summary>
    /// 64 karakterlik proje secret'i (<c>VITALWATCH_SECRET</c>). Yalnızca sunucuda bulunur;
    /// kaynak koda ya da depoya yazılmaz.
    /// </summary>
    public string? Secret { get; set; }

    /// <summary>
    /// Ortam: <c>production</c>, <c>staging</c> ya da <c>local</c> (<c>VITALWATCH_ENV</c>).
    /// Boşsa ASP.NET Core ortam adı kullanılır (<c>Development</c> → <c>local</c>).
    /// Takma adlar kardeş paketlerle aynı tabloyla çevrilir.
    /// </summary>
    public string? Env { get; set; }

    /// <summary>
    /// Sürüm etiketi (<c>VITALWATCH_RELEASE</c>). Boşsa CI değişkenlerinden ya da
    /// <c>.git</c>'ten kendiliğinden bulunur.
    /// </summary>
    public string? Release { get; set; }

    /// <summary>
    /// Gönderim zaman aşımı (<c>VITALWATCH_TIMEOUT</c>). 100 ve üstü milisaniye, altı saniye
    /// sayılır (<c>2</c> → 2000 ms). Sonuç 100–10000 ms aralığına sıkıştırılır; geçersiz,
    /// sıfır ya da negatif değer 2000 ms olur. Kural bütün VitalWatch paketlerinde aynı.
    /// </summary>
    public double? Timeout { get; set; }

    /// <summary>Bu süreyi aşan istek yavaş sayılır, ms (<c>VITALWATCH_SLOW_REQUEST_MS</c>). Varsayılan 1000.</summary>
    public int SlowRequestMs { get; set; } = 1000;

    /// <summary>Bu süreyi aşan SQL sorgusu yavaş sayılır, ms (<c>VITALWATCH_SLOW_QUERY_MS</c>). Varsayılan 500.</summary>
    public int SlowQueryMs { get; set; } = 500;

    /// <summary>
    /// Raporlanmayacak istisna türleri. Alt sınıflar da eşleşir.
    /// </summary>
    public IList<Type> Ignore { get; } = new List<Type>();

    /// <summary>
    /// Canlılık isteğinin aralığı. Varsayılan 8 saat — hub'ın 24 saatlik sessizlik eşiğinin
    /// üçte biri. <see cref="TimeSpan.Zero"/> canlılık isteğini kapatır.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromHours(8);
}
