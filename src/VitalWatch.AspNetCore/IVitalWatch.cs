namespace VitalWatch;

/// <summary>
/// Elle bildirim ve teşhis için enjekte edilebilir arayüz.
/// </summary>
/// <remarks>
/// DI dışından (statik bağlamdan) aynı işlemler <see cref="VitalWatchSdk"/> ile yapılır.
/// Hiçbir üye hata fırlatmaz.
/// </remarks>
public interface IVitalWatch
{
    /// <summary>İzleme etkin ve yapılandırılmış mı.</summary>
    bool Enabled { get; }

    /// <summary>
    /// Bir hatayı hub'a bildirir. Kuyruğa bırakır ve hemen döner — beklenmesi gerekmez,
    /// kullanıcı isteğini bekletmez.
    /// </summary>
    /// <param name="exception">Bildirilecek hata. Aynı nesne ikinci kez gönderilmez.</param>
    /// <param name="kind">
    /// Olay türü: <c>exception</c> (varsayılan), <c>fatal</c>, <c>job_failed</c>,
    /// <c>command_failed</c>. Tanınmayan değer <c>exception</c> sayılır.
    /// </param>
    /// <returns>Kuyruğa alındıysa <c>true</c>. Yok sayılan, daha önce bildirilmiş ya da
    /// kuyruk dolu olduğu için atılan hatada <c>false</c>.</returns>
    /// <remarks>
    /// Bir HTTP isteğinin içinden çağrılırsa rota ve yöntem kendiliğinden eklenir ve aynı istek
    /// 5xx ile biterse ayrıca stack'siz bir "HTTP 500" kaydı açılmaz.
    /// </remarks>
    bool Report(Exception exception, string? kind = null);

    /// <summary>
    /// "Buradayım" — olay taşımayan canlılık isteğini hemen gönderir ve sonucu döndürür.
    /// </summary>
    Task<ReportResult> HeartbeatAsync(CancellationToken cancellationToken = default);

    /// <summary>Kuyruktaki olayların gönderilmesini en çok <paramref name="timeout"/> kadar bekler.</summary>
    /// <returns>Kuyruk boşaldıysa <c>true</c>.</returns>
    Task<bool> FlushAsync(TimeSpan timeout);
}
