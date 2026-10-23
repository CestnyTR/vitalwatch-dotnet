namespace VitalWatch;

/// <summary>
/// VitalWatch configuration.
/// </summary>
/// <remarks>
/// Values are read in this order, each overriding the previous:
/// <list type="number">
/// <item>The <c>VitalWatch</c> section in <c>appsettings.json</c></item>
/// <item><c>VITALWATCH_*</c> variables in the process environment — the same rule as the
/// sibling packages: the process environment always wins over the file</item>
/// <item>Values passed in code via <c>AddVitalWatch(options =&gt; ...)</c></item>
/// </list>
/// </remarks>
public sealed class VitalWatchOptions
{
    /// <summary>
    /// When <c>false</c>, no hooks are installed and no data is sent
    /// (<c>VITALWATCH_ENABLED</c>). Default <c>true</c>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Hub URL, e.g. <c>https://hub.example.com</c> (<c>VITALWATCH_URL</c>).</summary>
    public string? Url { get; set; }

    /// <summary>Project key from the panel (<c>VITALWATCH_KEY</c>).</summary>
    public string? Key { get; set; }

    /// <summary>
    /// The 64-character project secret (<c>VITALWATCH_SECRET</c>). Keep it on the server only;
    /// never commit it to source code or a repository.
    /// </summary>
    public string? Secret { get; set; }

    /// <summary>
    /// Environment: <c>production</c>, <c>staging</c> or <c>local</c> (<c>VITALWATCH_ENV</c>).
    /// When empty, the ASP.NET Core environment name is used (<c>Development</c> → <c>local</c>).
    /// Aliases are mapped with the same table as the sibling packages.
    /// </summary>
    public string? Env { get; set; }

    /// <summary>
    /// Release tag (<c>VITALWATCH_RELEASE</c>). When empty, it is detected automatically from CI
    /// variables or <c>.git</c>.
    /// </summary>
    public string? Release { get; set; }

    /// <summary>
    /// Send timeout (<c>VITALWATCH_TIMEOUT</c>). Values of 100 and above are milliseconds, below
    /// that seconds (<c>2</c> → 2000 ms). The result is clamped to 100–10000 ms; an invalid,
    /// zero or negative value becomes 2000 ms. The rule is the same in all VitalWatch packages.
    /// </summary>
    public double? Timeout { get; set; }

    /// <summary>Requests taking longer than this are considered slow, in ms (<c>VITALWATCH_SLOW_REQUEST_MS</c>). Default 1000.</summary>
    public int SlowRequestMs { get; set; } = 1000;

    /// <summary>SQL queries taking longer than this are considered slow, in ms (<c>VITALWATCH_SLOW_QUERY_MS</c>). Default 500.</summary>
    public int SlowQueryMs { get; set; } = 500;

    /// <summary>
    /// Exception types that are not reported. Subclasses match as well.
    /// </summary>
    public IList<Type> Ignore { get; } = new List<Type>();

    /// <summary>
    /// Interval of the liveness request. Default 8 hours — a third of the hub's 24-hour silence
    /// threshold. <see cref="TimeSpan.Zero"/> disables the liveness request.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromHours(8);

    /// <summary>
    /// When <c>true</c>, the application's resolved NuGet package list (name and version only,
    /// read from <c>{EntryAssembly}.deps.json</c>) is sent to the hub once a day so that it can
    /// check it against known vulnerability advisories (<c>VITALWATCH_DEPENDENCIES</c>).
    /// Default <c>true</c>. <c>false</c> (or <c>0</c>/<c>no</c>/<c>off</c>) turns it off.
    /// </summary>
    public bool Dependencies { get; set; } = true;
}
