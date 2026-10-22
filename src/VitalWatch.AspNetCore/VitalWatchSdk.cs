using VitalWatch.Internal;

namespace VitalWatch;

/// <summary>
/// Manual reporting outside DI: <c>VitalWatchSdk.Report(ex)</c>.
/// </summary>
/// <remarks>
/// <para>Deliberately not named <c>VitalWatch</c>: a class with the same name as the namespace
/// would make <c>VitalWatch.Report(...)</c> resolve to the namespace in files with
/// <c>using VitalWatch;</c>, and it would not compile.</para>
/// <para>If <c>AddVitalWatch</c> was not called, monitoring is disabled or the host is not built
/// yet, calls do nothing and never throw. The injectable counterpart is
/// <see cref="IVitalWatch"/>.</para>
/// </remarks>
public static class VitalWatchSdk
{
    /// <summary>Whether monitoring is enabled (host built, configuration complete).</summary>
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
