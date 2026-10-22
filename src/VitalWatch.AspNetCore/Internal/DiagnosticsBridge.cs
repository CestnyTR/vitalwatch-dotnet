using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;

namespace VitalWatch.Internal;

/// <summary>
/// Süreç genelindeki tanılama kaynaklarını dinler: SQL komutları ve ASP.NET Core'un
/// yakaladığı istisnalar.
/// </summary>
/// <remarks>
/// <para><b>Neden DiagnosticListener.</b> EF Core, SqlClient ve ASP.NET Core olaylarını zaten
/// buraya yazıyor; dinlemek için bu kütüphanelere paket bağımlılığı gerekmez. Alternatifler
/// ya bağımlılık (EF <c>IDbCommandInterceptor</c> → EF Core paketi) ya da kullanıcıya ek
/// kurulum adımı (<c>AddInterceptors(...)</c>) getiriyordu — unutulan adım sessiz arızadır.</para>
///
/// <para><b>Kaynaklar.</b></para>
/// <list type="bullet">
/// <item><c>Microsoft.EntityFrameworkCore</c> — <c>CommandExecuting</c> / <c>CommandExecuted</c>
/// (yükte <c>Command</c> ve <c>Duration</c>). Sağlayıcıdan bağımsız: SQL Server, PostgreSQL,
/// SQLite, MySQL hepsi buradan geçer.</item>
/// <item><c>SqlClientDiagnosticListener</c> — Dapper ya da ham ADO.NET ile SQL Server.
/// <c>WriteCommandBefore</c>/<c>After</c> (Microsoft.Data.SqlClient ve System.Data.SqlClient);
/// süre iki olayın <c>Timestamp</c> farkından.</item>
/// <item><c>Npgsql</c> ActivitySource — Dapper ya da ham ADO.NET ile PostgreSQL.
/// Npgsql DiagnosticListener yazmıyor, yalnızca Activity üretiyor.</item>
/// <item><c>Microsoft.AspNetCore</c> — hata işleyicisi ve geliştirici sayfasının yakaladığı
/// istisnalar; ara katman onlardan önce eklenmişse istisnayı göremiyordu.</item>
/// </list>
///
/// <para><b>Çift sayım.</b> EF Core + SqlClient aynı komutu iki kez bildirir. EF'nin
/// <c>CommandExecuting</c> olayında komut nesnesi zayıf tabloya işaretlenir; SqlClient o
/// komutu atlar. Npgsql'in Activity'si komut nesnesini taşımıyor; o yüzden istek içinde EF
/// görüldüyse (ya da istek dışında süreçte EF hiç görüldüyse) Npgsql izi atlanır. Bilinen
/// taviz: aynı istekte hem EF hem ham Npgsql (Dapper) kullanan kodda EF'den sonraki ham
/// sorgular sayılmaz.</para>
///
/// <para><b>Tekil abonelik.</b> <see cref="DiagnosticListener.AllListeners"/> süreç geneli;
/// her host ayrı abone olsaydı aynı sorgu her host için bir kez raporlanırdı. Olay, istek
/// kapsamı varsa o kapsamın raporlayıcısına, yoksa en son kaydolan raporlayıcıya gider.</para>
/// </remarks>
internal static class DiagnosticsBridge
{
    public const string EfListener = "Microsoft.EntityFrameworkCore";
    public const string EfCommandExecuting = "Microsoft.EntityFrameworkCore.Database.Command.CommandExecuting";
    public const string EfCommandExecuted = "Microsoft.EntityFrameworkCore.Database.Command.CommandExecuted";

    public const string SqlClientListener = "SqlClientDiagnosticListener";
    public const string NpgsqlSource = "Npgsql";
    public const string AspNetCoreListener = "Microsoft.AspNetCore";
    public const string HostingUnhandledException = "Microsoft.AspNetCore.Hosting.UnhandledException";

    private static readonly object Gate = new();
    private static readonly List<Reporter> Reporters = [];
    private static readonly ConditionalWeakTable<object, object> EfCommands = new();
    private static readonly ConcurrentDictionary<Guid, long> SqlClientStarts = new();

    private static IDisposable? _allListeners;
    private static ActivityListener? _activityListener;
    private static bool _appDomainHooked;
    private static volatile bool _efSeen;

    /// <summary>Etkin raporlayıcı var mı — dinleyiciler her olayda ilk buna bakar.</summary>
    private static volatile bool _active;

    public static void Register(Reporter reporter)
    {
        try
        {
            lock (Gate)
            {
                Reporters.Remove(reporter);
                Reporters.Add(reporter);
                _active = true;

                _allListeners ??= DiagnosticListener.AllListeners.Subscribe(new AllListenersObserver());

                if (_activityListener is null)
                {
                    _activityListener = new ActivityListener
                    {
                        ShouldListenTo = source => source.Name == NpgsqlSource,
                        // Etiketler (SQL metni) yalnızca AllData'da doluyor.
                        Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                            _active ? ActivitySamplingResult.AllData : ActivitySamplingResult.None,
                        ActivityStopped = OnNpgsqlActivityStopped,
                    };

                    ActivitySource.AddActivityListener(_activityListener);
                }

                if (!_appDomainHooked)
                {
                    _appDomainHooked = true;
                    AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                }
            }
        }
        catch
        {
            // Dinleyici kurulamadıysa izleme eksik çalışır; uygulama etkilenmez.
        }
    }

    public static void Unregister(Reporter reporter)
    {
        lock (Gate)
        {
            Reporters.Remove(reporter);
            _active = Reporters.Count > 0;
        }
    }

    /// <summary>İstek kapsamı varsa onun raporlayıcısı, yoksa en son kaydolan.</summary>
    private static Reporter? Target(RequestScope? scope)
    {
        if (!_active)
        {
            return null;
        }

        if (scope is not null)
        {
            return scope.Reporter.Active ? scope.Reporter : null;
        }

        lock (Gate)
        {
            return Reporters.Count > 0 ? Reporters[^1] : null;
        }
    }

    // ------------------------------------------------------------------
    // EF Core
    // ------------------------------------------------------------------

    internal static void OnEf(string name, object? payload)
    {
        if (!_active)
        {
            return;
        }

        try
        {
            var scope = RequestScope.Current;

            if (name == EfCommandExecuting)
            {
                _efSeen = true;

                if (scope is not null)
                {
                    scope.EfSeen = true;
                }

                // SqlClient aynı komutu ayrıca bildirecek; EF'nin olayı yeterli.
                if (PropertyFetcher.Fetch(payload, "Command") is { } command)
                {
                    EfCommands.AddOrUpdate(command, Marker);
                }

                return;
            }

            if (name == EfCommandExecuted)
            {
                var sql = (PropertyFetcher.Fetch(payload, "Command") as DbCommand)?.CommandText;
                var duration = PropertyFetcher.Fetch(payload, "Duration") is TimeSpan d ? d.TotalMilliseconds : 0;

                Target(scope)?.RecordQuery(sql, duration, scope);
            }
        }
        catch
        {
            // Sessiz.
        }
    }

    private static readonly object Marker = new();

    // ------------------------------------------------------------------
    // SqlClient
    // ------------------------------------------------------------------

    internal static void OnSqlClient(string name, object? payload)
    {
        if (!_active)
        {
            return;
        }

        try
        {
            if (!name.EndsWith("WriteCommandBefore", StringComparison.Ordinal)
                && !name.EndsWith("WriteCommandAfter", StringComparison.Ordinal)
                && !name.EndsWith("WriteCommandError", StringComparison.Ordinal))
            {
                return;
            }

            var command = PropertyFetcher.Fetch(payload, "Command");

            // EF Core'un komutu: EF olayı zaten sayıyor.
            if (command is not null && EfCommands.TryGetValue(command, out _))
            {
                return;
            }

            if (PropertyFetcher.Fetch(payload, "OperationId") is not Guid operation)
            {
                return;
            }

            if (name.EndsWith("WriteCommandBefore", StringComparison.Ordinal))
            {
                // Bitmeyen komutlar (iptal, süreç dışı hata) birikmesin.
                if (SqlClientStarts.Count > 10_000)
                {
                    SqlClientStarts.Clear();
                }

                SqlClientStarts[operation] = Timestamp(payload);

                return;
            }

            if (!SqlClientStarts.TryRemove(operation, out var started) || name.EndsWith("WriteCommandError", StringComparison.Ordinal))
            {
                return;
            }

            var elapsed = Stopwatch.GetElapsedTime(started, Timestamp(payload)).TotalMilliseconds;
            var scope = RequestScope.Current;

            Target(scope)?.RecordQuery((command as DbCommand)?.CommandText, elapsed, scope);
        }
        catch
        {
            // Sessiz.
        }
    }

    private static long Timestamp(object? payload) =>
        PropertyFetcher.Fetch(payload, "Timestamp") is long ticks && ticks > 0 ? ticks : Stopwatch.GetTimestamp();

    // ------------------------------------------------------------------
    // Npgsql (ActivitySource)
    // ------------------------------------------------------------------

    internal static void OnNpgsqlActivityStopped(Activity activity)
    {
        if (!_active)
        {
            return;
        }

        try
        {
            var sql = activity.GetTagItem("db.query.text") as string ?? activity.GetTagItem("db.statement") as string;
            if (string.IsNullOrEmpty(sql))
            {
                return;
            }

            var scope = RequestScope.Current;

            // EF Core aynı komutu zaten bildirdi (bkz. sınıf açıklaması).
            if (scope is not null ? scope.EfSeen : _efSeen)
            {
                return;
            }

            Target(scope)?.RecordQuery(sql, activity.Duration.TotalMilliseconds, scope);
        }
        catch
        {
            // Sessiz.
        }
    }

    // ------------------------------------------------------------------
    // ASP.NET Core istisnaları
    // ------------------------------------------------------------------

    /// <summary>
    /// Hata işleyicisi / geliştirici sayfası / barındırma katmanının yakaladığı istisna.
    /// </summary>
    /// <remarks>
    /// Burada GÖNDERİLMEZ, isteğin kapsamına bırakılır: ara katman yanıt bittiğinde durum
    /// koduna bakar. Uygulamanın <c>IExceptionHandler</c>'ı istisnayı 400'e çevirdiyse arıza
    /// değildir ve atılır; 5xx ile bittiyse stack'iyle raporlanır.
    ///
    /// Kapsam yoksa (ara katman eklenmemiş) yalnızca barındırma katmanına kadar çıkan istisna
    /// doğrudan raporlanır: oraya ulaşan istisna kesin 500'dür.
    /// </remarks>
    internal static void OnAspNetCore(string name, object? payload)
    {
        if (!_active)
        {
            return;
        }

        try
        {
            if (!name.EndsWith("UnhandledException", StringComparison.Ordinal)
                && !name.EndsWith("HandledException", StringComparison.Ordinal))
            {
                return;
            }

            if (PropertyFetcher.Fetch(payload, "exception") is not Exception exception
                || PropertyFetcher.Fetch(payload, "httpContext") is not HttpContext context)
            {
                return;
            }

            if (Reporter.IsMarked(exception))
            {
                return;
            }

            if (context.Items.TryGetValue(RequestScope.ItemKey, out var item) && item is RequestScope scope)
            {
                if (scope.PendingException is null)
                {
                    scope.PendingException = exception;
                    scope.PendingRoute = RouteDescriber.Describe(context);
                }

                return;
            }

            if (name == HostingUnhandledException && !context.RequestAborted.IsCancellationRequested)
            {
                Target(null)?.RecordException(exception, null, context);
            }
        }
        catch
        {
            // Sessiz.
        }
    }

    // ------------------------------------------------------------------
    // Süreci düşüren istisna
    // ------------------------------------------------------------------

    /// <summary>
    /// Süreci düşüren yakalanmamış istisna (arka plan thread'i, <c>async void</c>).
    /// </summary>
    /// <remarks>
    /// Davranış değiştirilmez: süreç yine düşer. Süreç ölmek üzere olduğu için kuyruk burada
    /// kısa süreli BEKLENİR — tek istisna; beklenmezse olay hiç gitmez.
    /// </remarks>
    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs args)
    {
        try
        {
            if (args.ExceptionObject is not Exception exception || Target(null) is not { } reporter)
            {
                return;
            }

            if (reporter.RecordException(exception, exception is OutOfMemoryException ? "fatal" : "exception", useScope: false)
                && args.IsTerminating)
            {
                reporter.FlushAsync(TimeSpan.FromMilliseconds(reporter.Settings.TimeoutMs + 200))
                    .GetAwaiter()
                    .GetResult();
            }
        }
        catch
        {
            // Sessiz.
        }
    }

    // ------------------------------------------------------------------
    // Abonelik
    // ------------------------------------------------------------------

    private sealed class AllListenersObserver : IObserver<DiagnosticListener>
    {
        public void OnNext(DiagnosticListener listener)
        {
            try
            {
                switch (listener.Name)
                {
                    case EfListener:
                        listener.Subscribe(
                            new EventObserver(OnEf),
                            static name => name is EfCommandExecuting or EfCommandExecuted);
                        break;

                    case SqlClientListener:
                        listener.Subscribe(
                            new EventObserver(OnSqlClient),
                            static name => name.EndsWith("WriteCommandBefore", StringComparison.Ordinal)
                                || name.EndsWith("WriteCommandAfter", StringComparison.Ordinal)
                                || name.EndsWith("WriteCommandError", StringComparison.Ordinal));
                        break;

                    case AspNetCoreListener:
                        listener.Subscribe(
                            new EventObserver(OnAspNetCore),
                            static name => name.EndsWith("HandledException", StringComparison.Ordinal)
                                || name.EndsWith("UnhandledException", StringComparison.Ordinal));
                        break;
                }
            }
            catch
            {
                // Sessiz.
            }
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }

    private sealed class EventObserver(Action<string, object?> handler) : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> value) => handler(value.Key, value.Value);

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
