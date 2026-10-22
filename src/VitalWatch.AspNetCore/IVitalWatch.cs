namespace VitalWatch;

/// <summary>
/// Injectable interface for manual reporting and diagnostics.
/// </summary>
/// <remarks>
/// Outside DI (from a static context) the same operations are available through
/// <see cref="VitalWatchSdk"/>. No member throws.
/// </remarks>
public interface IVitalWatch
{
    /// <summary>Whether monitoring is enabled and configured.</summary>
    bool Enabled { get; }

    /// <summary>
    /// Reports an exception to the hub. Queues it and returns immediately — no need to await,
    /// and the user's request is never held up.
    /// </summary>
    /// <param name="exception">The exception to report. The same instance is never sent twice.</param>
    /// <param name="kind">
    /// Event kind: <c>exception</c> (default), <c>fatal</c>, <c>job_failed</c>,
    /// <c>command_failed</c>. An unrecognized value is treated as <c>exception</c>.
    /// </param>
    /// <returns><c>true</c> if queued; <c>false</c> if the exception is ignored, was already
    /// reported, or was dropped because the queue is full.</returns>
    /// <remarks>
    /// When called from within an HTTP request, the route and method are added automatically,
    /// and if the same request ends with a 5xx no separate stack-less "HTTP 500" event is created.
    /// </remarks>
    bool Report(Exception exception, string? kind = null);

    /// <summary>
    /// "I am here" — immediately sends a liveness request that carries no events and returns
    /// the result.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<ReportResult> HeartbeatAsync(CancellationToken cancellationToken = default);

    /// <summary>Waits at most <paramref name="timeout"/> for queued events to be sent.</summary>
    /// <param name="timeout">The maximum time to wait.</param>
    /// <returns><c>true</c> if the queue was drained.</returns>
    Task<bool> FlushAsync(TimeSpan timeout);
}
