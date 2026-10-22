namespace VitalWatch;

/// <summary>
/// The result of a send. Only the diagnostics command and tests read it; the monitoring path
/// ignores it.
/// </summary>
/// <param name="Sent">Whether the request reached the hub (HTTP &lt; 400). The hub returns 204
/// even for an invalid signature; <c>true</c> means "the request arrived", not "it was
/// accepted".</param>
/// <param name="Status">The HTTP status code; <c>null</c> on a network error.</param>
/// <param name="Error">The reason, if the request did not arrive.</param>
public readonly record struct ReportResult(bool Sent, int? Status, string? Error)
{
    internal static ReportResult Failed(string error) => new(false, null, error);
}
