using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;

namespace VitalWatch.Internal;

/// <summary>
/// Olayları kurar, kuyruğa bırakır ve arka planda hub'a gönderir.
/// </summary>
/// <remarks>
/// Süreç boyunca tekil (DI singleton); istek bağlamı <see cref="RequestScope"/> ile taşınır.
///
/// Gönderim <b>sınırlı</b> bir kuyruktan tek bir arka plan görevinden yapılır (Python
/// paketiyle aynı yapı): kullanıcı isteği hiçbir koşulda beklemez. Kuyruk dolarsa olay
/// düşürülür — hata fırtınasında sınırsız büyüyen kuyruk izlenen uygulamanın belleğini
/// yerdi; izleme paketinin izlediği uygulamayı öldürmesi, çözdüğü sorundan büyük bir sorundur.
///
/// Her genel üye kendi hatasını yutar: paket kendi hatasını raporlamaz (sonsuz döngü) ve
/// izlenen uygulamaya hata sızdırmaz.
/// </remarks>
internal sealed class Reporter : IVitalWatch, IDisposable, IAsyncDisposable
{
    /// <summary>Kuyruk derinliği; hub'ın istek başına aldığı 8 olayın epey üstünde.</summary>
    public const int QueueSize = 100;

    /// <summary>
    /// İlk olaydan sonra kısa bekleme: aynı anda düşen olaylar (hata + yavaş sorgu …) tek
    /// istekte gider. Hub'ın IP başına 60/dk sınırı var; hata fırtınasında her olaya bir
    /// istek, sınırı saniyeler içinde doldururdu.
    /// </summary>
    private static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(50);

    private const string ReportedKey = "VitalWatch.Reported";

    private static readonly ConditionalWeakTable<Exception, object> ReportedTable = new();

    private static readonly Regex SqlTypeName = new(
        "sql|database|dbupdate|dbexception|npgsql|postgres|mysql|sqlite|oracle",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SqlKeyword = new(
        @"\b(?:select|insert|update|delete)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static volatile Reporter? _current;

    private readonly Channel<HubEvent> _channel = Channel.CreateBounded<HubEvent>(
        new BoundedChannelOptions(QueueSize)
        {
            FullMode = BoundedChannelFullMode.Wait, // TryWrite dolu kuyrukta false döner: düşürülür.
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly object _gate = new();
    private readonly HubClient _client;
    private Task? _sender;
    private int _inFlight;
    private long _dropped;
    private volatile bool _closing;
    private int _disposed;

    public Reporter(Settings settings)
    {
        Settings = settings;
        _client = new HubClient(settings);

        if (settings.Active)
        {
            _current = this;
        }
    }

    /// <summary>Statik yüz (<see cref="VitalWatchSdk"/>) için en son kurulan etkin raporlayıcı.</summary>
    public static Reporter? Current => _current;

    public Settings Settings { get; }

    /// <summary>Paylaşılan imzalı istemci — bağımlılık listesi de aynı bağlantı havuzundan gider.</summary>
    internal HubClient Client => _client;

    /// <summary>Etkin ve yapılandırılmış.</summary>
    public bool Active => Settings.Active && Volatile.Read(ref _disposed) == 0;

    public bool Enabled => Active;

    /// <summary>Kuyruk dolu olduğu için atılan olay sayısı.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    // ------------------------------------------------------------------
    // Kayıt
    // ------------------------------------------------------------------

    public bool Report(Exception exception, string? kind = null)
    {
        var normalized = kind is not null && Array.IndexOf(HubEvent.Kinds, kind) >= 0 ? kind : null;

        return RecordException(exception, normalized);
    }

    /// <summary>
    /// Bir istisnayı raporlar. Hiçbir koşulda fırlatmaz.
    /// </summary>
    /// <param name="exception">İstisna.</param>
    /// <param name="kind">Tür; <c>null</c> ise istisnadan seçilir.</param>
    /// <param name="context">HTTP isteği; <c>null</c> ise mevcut kapsamdan bulunur.</param>
    /// <param name="route">Rota etiketi; verilmezse istekten hesaplanır.</param>
    /// <param name="useScope"><c>false</c> ise istek kapsamına bakılmaz (arka plan işi).</param>
    public bool RecordException(
        Exception exception,
        string? kind = null,
        HttpContext? context = null,
        string? route = null,
        bool useScope = true)
    {
        try
        {
            if (!Active || exception is null || Ignored(exception) || !Mark(exception))
            {
                return false;
            }

            var scope = useScope ? FindScope(context) : null;
            context ??= scope?.Context;

            if (scope is not null)
            {
                // Aynı istek 5xx ile biterse ayrıca stack'siz "HTTP 500" açılmasın.
                scope.Reported = true;
            }

            var label = route ?? (context is not null ? RouteDescriber.Describe(context) : null);

            var e = BuildException(exception, kind ?? KindFor(exception))
                .Set("route", label)
                .Set("method", useScope ? context?.Request.Method : null);

            if (scope is not null)
            {
                AddQueries(e, scope);
            }

            return Enqueue(e);
        }
        catch
        {
            // Kendi hatasını raporlamaz — sonsuz döngü riski.
            return false;
        }
    }

    /// <summary>
    /// Çöken arka plan servisi. Rota yerine servisin türü gönderilir: panelde hangi işin
    /// düştüğü "Yol" sütununda görünür.
    /// </summary>
    public bool RecordJobFailed(Exception exception, string job) =>
        RecordException(exception, "job_failed", context: null, route: Scrubber.Truncate(job, 300), useScope: false);

    /// <summary>
    /// İstek bitiminde çağrılır. Yalnızca yavaş istek veya 5xx raporlanır; her isteği
    /// göndermek izlenen uygulamaya da hub'a da yük olurdu.
    /// </summary>
    public void RecordRequest(RequestScope scope, int status, double durationMs)
    {
        try
        {
            if (!Active)
            {
                return;
            }

            /*
             * İç katmandaki hata işleyici istisnayı yakaladı ve yanıt 5xx ile bitti: istisna
             * stack'iyle raporlanır, ayrıca "HTTP 500" açılmaz. İşleyici onu 4xx'e çevirdiyse
             * (doğrulama hatası …) arıza değil, atılır.
             */
            if (status >= 500 && scope.PendingException is { } pending && !scope.Reported)
            {
                RecordException(pending, null, scope.Context, scope.PendingRoute);
            }

            var slow = durationMs >= Settings.SlowRequestMs;
            if (!slow && status < 500)
            {
                return;
            }

            // Hata stack'iyle raporlandı; "HTTP 500" aynı arızanın tekrarı olur.
            if (status >= 500 && scope.Reported)
            {
                return;
            }

            var label = RouteDescriber.Describe(scope.Context);
            var ms = (long)Math.Round(durationMs);

            var e = new HubEvent(
                    status >= 500 ? "http_5xx" : "slow_request",
                    status >= 500 ? $"{label} — HTTP {status}" : $"{label} — {ms} ms")
                .Set("route", label)
                .Set("method", scope.Context.Request.Method)
                .Set("status", status)
                .Set("duration_ms", ms)
                .Set("memory_mb", MemoryMb());

            AddQueries(e, scope);
            Enqueue(e);
        }
        catch
        {
            // Sessiz.
        }
    }

    /// <summary>
    /// Bir SQL komutu tamamlandı. İstek içindeyse sayaca eklenir; eşiği aşarsa ayrıca
    /// <c>slow_query</c> gönderilir.
    /// </summary>
    public void RecordQuery(string? sql, double durationMs, RequestScope? scope)
    {
        try
        {
            if (!Active || string.IsNullOrEmpty(sql))
            {
                return;
            }

            scope?.RecordQuery(sql, durationMs);

            if (durationMs < Settings.SlowQueryMs)
            {
                return;
            }

            // Normalize edilmiş SQL mesaj olarak kullanılır: aynı sorgu farklı
            // parametrelerle çalıştığında tek grupta toplanır.
            var normalized = Scrubber.Sql(sql) ?? "?";

            var e = new HubEvent("slow_query", normalized)
                .Set("slowest_query_sql", normalized)
                .Set("slowest_query_ms", (long)Math.Round(durationMs));

            if (scope is not null)
            {
                e.Set("route", RouteDescriber.Describe(scope.Context))
                    .Set("method", scope.Context.Request.Method);
            }

            Enqueue(e);
        }
        catch
        {
            // Sessiz.
        }
    }

    public async Task<ReportResult> HeartbeatAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Active)
            {
                return ReportResult.Failed(Settings.Enabled ? "missing-config" : "disabled");
            }

            return await _client.SendAsync(PayloadEncoder.Heartbeat(Settings), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return ReportResult.Failed(e.Message);
        }
    }

    /// <summary>
    /// Kuyruğu atlayıp tek olayı hemen gönderir ve sonucu döndürür. Yalnızca teşhis komutu
    /// kullanır: "gönderildi" demeden önce sonucun okunması gerekiyor.
    /// </summary>
    public async Task<ReportResult> SendNowAsync(Exception exception)
    {
        try
        {
            if (!Active)
            {
                return ReportResult.Failed(Settings.Enabled ? "missing-config" : "disabled");
            }

            var (body, _) = PayloadEncoder.Encode([BuildException(exception, "exception")], Settings);

            return body is null
                ? ReportResult.Failed("body-too-large")
                : await _client.SendAsync(body).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return ReportResult.Failed(e.Message);
        }
    }

    // ------------------------------------------------------------------
    // Kuyruk ve gönderim
    // ------------------------------------------------------------------

    internal bool Enqueue(HubEvent e)
    {
        if (!Active || _closing)
        {
            return false;
        }

        EnsureSender();

        Interlocked.Increment(ref _inFlight);

        if (_channel.Writer.TryWrite(e))
        {
            return true;
        }

        Interlocked.Decrement(ref _inFlight);
        Interlocked.Increment(ref _dropped);

        return false;
    }

    private void EnsureSender()
    {
        if (_sender is not null)
        {
            return;
        }

        lock (_gate)
        {
            if (_sender is not null)
            {
                return;
            }

            /*
             * Bağlam akışı kesilir: görev ilk olayı kuyruğa bırakan isteğin içinde
             * başlıyor. Akış kesilmeseydi o isteğin AsyncLocal kapsamını ve Activity'sini
             * süreç boyunca taşır, hub'a giden her istek o isteğin izine çocuk olarak düşerdi.
             */
            using (ExecutionContext.SuppressFlow())
            {
                _sender = Task.Run(SendLoopAsync);
            }
        }
    }

    private async Task SendLoopAsync()
    {
        var reader = _channel.Reader;
        var pending = new List<HubEvent>(PayloadEncoder.MaxEvents * 2);

        while (true)
        {
            try
            {
                if (!await reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    return;
                }

                if (!_closing && reader.Count < PayloadEncoder.MaxEvents)
                {
                    await Task.Delay(BatchWindow).ConfigureAwait(false);
                }

                Drain(reader, pending);

                while (pending.Count > 0)
                {
                    var (body, consumed) = PayloadEncoder.Encode(pending, Settings);
                    pending.RemoveRange(0, consumed);

                    try
                    {
                        if (body is not null)
                        {
                            await _client.SendAsync(body).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        Interlocked.Add(ref _inFlight, -consumed);
                    }

                    Drain(reader, pending);
                }
            }
            catch
            {
                /*
                 * HubClient zaten yutuyor; bu ikinci ağ yalnızca döngünün ölmemesi için —
                 * ölürse sonraki olaylar sessizce birikir. Elde kalanlar atılır.
                 */
                Interlocked.Add(ref _inFlight, -pending.Count);
                pending.Clear();
            }
        }
    }

    private static void Drain(ChannelReader<HubEvent> reader, List<HubEvent> pending)
    {
        while (pending.Count < PayloadEncoder.MaxEvents * 2 && reader.TryRead(out var e))
        {
            pending.Add(e);
        }
    }

    public async Task<bool> FlushAsync(TimeSpan timeout)
    {
        try
        {
            var watch = Stopwatch.StartNew();

            while (Volatile.Read(ref _inFlight) > 0)
            {
                if (watch.Elapsed >= timeout)
                {
                    return false;
                }

                await Task.Delay(10).ConfigureAwait(false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Kapanışta kuyruğun boşalması için tanınan süre.</summary>
    private TimeSpan ShutdownTimeout => TimeSpan.FromMilliseconds(Math.Min(Settings.TimeoutMs + 500, 3000));

    public void Dispose()
    {
        if (!BeginDispose())
        {
            return;
        }

        try
        {
            _sender?.Wait(ShutdownTimeout);
        }
        catch
        {
            // Sessiz.
        }

        EndDispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (!BeginDispose())
        {
            return;
        }

        try
        {
            if (_sender is not null)
            {
                await _sender.WaitAsync(ShutdownTimeout).ConfigureAwait(false);
            }
        }
        catch
        {
            // Sessiz.
        }

        EndDispose();
    }

    private bool BeginDispose()
    {
        // Kuyruktakiler gider, yenisi alınmaz.
        _closing = true;
        _channel.Writer.TryComplete();

        return Interlocked.Exchange(ref _disposed, 1) == 0;
    }

    private void EndDispose()
    {
        Interlocked.CompareExchange(ref _current, null, this);
        DiagnosticsBridge.Unregister(this);
        _client.Dispose();
    }

    // ------------------------------------------------------------------
    // Yardımcılar
    // ------------------------------------------------------------------

    private static RequestScope? FindScope(HttpContext? context)
    {
        if (context is not null && context.Items.TryGetValue(RequestScope.ItemKey, out var item) && item is RequestScope fromItems)
        {
            return fromItems;
        }

        var current = RequestScope.Current;

        return context is null || current?.Context == context ? current : null;
    }

    private static void AddQueries(HubEvent e, RequestScope scope)
    {
        var (count, slowestMs, slowestSql) = scope.Queries();
        if (count == 0)
        {
            return;
        }

        e.Set("query_count", count);

        if (e.Get("slowest_query_ms") is null && slowestMs is { } ms)
        {
            e.Set("slowest_query_ms", (long)Math.Round(ms));
            e.Set("slowest_query_sql", Scrubber.Sql(slowestSql));
        }
    }

    private HubEvent BuildException(Exception exception, string kind)
    {
        var containsSql = ContainsSql(exception);
        var (file, line) = Location(exception);

        return new HubEvent(
                kind,
                // Veritabanı hataları SQL'i bağlanmış değerlerle taşır; oturum kimliği,
                // e-posta, kart numarası oradan sızabilir.
                Scrubber.Message(exception.Message, containsSql) ?? exception.GetType().Name)
            .Set("exception_class", exception.GetType().FullName ?? exception.GetType().Name)
            .Set("stack", Scrubber.Stack(FormatStack(exception)))
            .Set("file", Scrubber.Text(file, 300))
            .Set("line", line);
    }

    /// <summary>
    /// <c>Exception.ToString()</c> benzeri ama iç istisnaların MESAJLARI da temizlenerek.
    /// </summary>
    /// <remarks>
    /// <c>ToString()</c> iç istisnanın mesajını ham taşır: <c>DbUpdateException</c>'ın içindeki
    /// sürücü hatası SQL'i bağlanmış değerlerle verir. Genel maskeleme e-postayı yakalar ama
    /// tırnak içindeki keyfi değeri yakalayamaz; bu yüzden her mesaj ayrıca SQL temizliğinden
    /// geçer.
    /// </remarks>
    internal static string FormatStack(Exception exception)
    {
        var builder = new StringBuilder();
        var depth = 0;

        for (var current = exception; current is not null && depth < 5; current = current.InnerException, depth++)
        {
            if (depth > 0)
            {
                builder.Append(" ---> ");
            }

            builder.Append(current.GetType().FullName)
                .Append(": ")
                .Append(Scrubber.Message(current.Message, ContainsSql(current)))
                .Append('\n');

            if (current.StackTrace is { } trace)
            {
                builder.Append(trace).Append('\n');
            }
        }

        return builder.ToString();
    }

    /// <summary>İlk satır bilgili çerçeve; pdb yoksa ikisi de <c>null</c>.</summary>
    private static (string? File, int? Line) Location(Exception exception)
    {
        try
        {
            var frames = new StackTrace(exception, fNeedFileInfo: true).GetFrames();

            foreach (var frame in frames)
            {
                var file = frame.GetFileName();
                if (!string.IsNullOrEmpty(file))
                {
                    var line = frame.GetFileLineNumber();

                    return (file, line > 0 ? line : null);
                }
            }
        }
        catch
        {
            // Satır bilgisi yok.
        }

        return (null, null);
    }

    /// <summary>
    /// <c>fatal</c> ayrı bir tür: çalışma zamanının sınırına çarpmak, uygulamanın
    /// yakalayabildiği bir hatadan başka bir şeydir ve panelde ayrı süzülmesi gerekir.
    /// </summary>
    private static string KindFor(Exception exception) =>
        exception is OutOfMemoryException or InsufficientExecutionStackException ? "fatal" : "exception";

    /// <summary>
    /// Mesajı SQL taşıyan hatalar. Sürücü paketlerine bağımlılık kurulmadığı için ada ve
    /// içeriğe bakılıyor.
    /// </summary>
    internal static bool ContainsSql(Exception exception)
    {
        try
        {
            return exception is System.Data.Common.DbException
                || SqlTypeName.IsMatch(exception.GetType().FullName ?? string.Empty)
                || SqlKeyword.IsMatch(exception.Message ?? string.Empty);
        }
        catch
        {
            return true;
        }
    }

    private bool Ignored(Exception exception)
    {
        // 4xx taşıyan hata çağıranın hatası, arıza değil.
        if (exception is BadHttpRequestException bad && bad.StatusCode < 500)
        {
            return true;
        }

        foreach (var type in Settings.Ignore)
        {
            if (type.IsInstanceOfType(exception))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Daha önce raporlandıysa <c>false</c>. İşaret istisnanın üzerinde (<c>Exception.Data</c>)
    /// taşınır: ASP.NET Core'un kendi günlüğü, hata işleyicisi ve tanılama olayları aynı
    /// istisnayı birden çok yoldan getirir. <c>Data</c> salt okunursa zayıf tablo korur.
    /// </summary>
    internal static bool Mark(Exception exception)
    {
        if (IsMarked(exception))
        {
            return false;
        }

        try
        {
            exception.Data[ReportedKey] = true;
        }
        catch
        {
            // Salt okunur Data — tablo tek başına korur.
        }

        ReportedTable.AddOrUpdate(exception, true);

        return true;
    }

    internal static bool IsMarked(Exception exception)
    {
        try
        {
            if (exception.Data.Contains(ReportedKey))
            {
                return true;
            }
        }
        catch
        {
            // Sessiz.
        }

        return ReportedTable.TryGetValue(exception, out _);
    }

    private static long MemoryMb()
    {
        try
        {
            return (long)Math.Round(Environment.WorkingSet / 1048576d);
        }
        catch
        {
            return 0;
        }
    }
}
