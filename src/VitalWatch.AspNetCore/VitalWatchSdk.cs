using VitalWatch.Internal;

namespace VitalWatch;

/// <summary>
/// DI dışından elle bildirim: <c>VitalWatchSdk.Report(ex)</c>.
/// </summary>
/// <remarks>
/// <para>Ad bilinçli olarak <c>VitalWatch</c> değil: ad alanıyla aynı adı taşıyan bir sınıf
/// <c>using VitalWatch;</c> yazan dosyada <c>VitalWatch.Report(...)</c> çağrısını ad alanına
/// çözer ve derlenmez.</para>
/// <para><c>AddVitalWatch</c> çağrılmamışsa, izleme kapalıysa ya da host henüz kurulmamışsa
/// çağrılar hiçbir şey yapmaz ve hata fırlatmaz. Enjekte edilebilir karşılığı
/// <see cref="IVitalWatch"/>.</para>
/// </remarks>
public static class VitalWatchSdk
{
    /// <summary>İzleme etkin mi (host kurulmuş, yapılandırma tamam).</summary>
    public static bool Enabled => Reporter.Current?.Active == true;

    /// <inheritdoc cref="IVitalWatch.Report"/>
    public static bool Report(Exception exception, string? kind = null) =>
        Reporter.Current?.Report(exception, kind) ?? false;

    /// <inheritdoc cref="IVitalWatch.HeartbeatAsync"/>
    public static Task<ReportResult> HeartbeatAsync(CancellationToken cancellationToken = default) =>
        Reporter.Current?.HeartbeatAsync(cancellationToken) ?? Task.FromResult(ReportResult.Failed("not-initialized"));

    /// <inheritdoc cref="IVitalWatch.FlushAsync"/>
    public static Task<bool> FlushAsync(TimeSpan timeout) =>
        Reporter.Current?.FlushAsync(timeout) ?? Task.FromResult(true);
}
