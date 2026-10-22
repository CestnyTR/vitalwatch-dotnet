using Microsoft.AspNetCore.Http;

namespace VitalWatch.Internal;

/// <summary>
/// Tek bir HTTP isteğinin ölçüm durumu: sorgu sayacı, en yavaş sorgu, bekleyen istisna.
/// </summary>
/// <remarks>
/// <see cref="AsyncLocal{T}"/> ile taşınır: ara katman isteğin başında kurar, istek boyunca
/// açılan her <c>await</c> zinciri aynı nesneyi görür, eşzamanlı istekler birbirininkini
/// görmez. Süreç boyunca tekil bir sayaç kullanmak eşzamanlı isteklerin sorgularını
/// birbirine karıştırırdı.
///
/// Laravel paketinde Recorder istek başına tekil; burada süreç tek, istek bağlamı bu nesne.
/// </remarks>
internal sealed class RequestScope
{
    /// <summary>Aynı isteğin yeniden yürütülmesinde (hata sayfası) kapsam tekrar kurulmaz.</summary>
    public const string ItemKey = "VitalWatch.RequestScope";

    private static readonly AsyncLocal<RequestScope?> CurrentScope = new();

    private readonly object _gate = new();
    private int _queryCount;
    private double _slowestMs = -1;
    private string? _slowestSql;

    public RequestScope(HttpContext context, Reporter reporter)
    {
        Context = context;
        Reporter = reporter;
    }

    public static RequestScope? Current
    {
        get => CurrentScope.Value;
        set => CurrentScope.Value = value;
    }

    public HttpContext Context { get; }

    public Reporter Reporter { get; }

    /// <summary>Bu istek için bir hata raporlandı; aynı arıza için "HTTP 500" açılmaz.</summary>
    public volatile bool Reported;

    /// <summary>
    /// Bu istekte EF Core komutu görüldü. Görüldüyse Npgsql'in kendi izi aynı sorguyu
    /// ikinci kez saymasın diye atlanır.
    /// </summary>
    public volatile bool EfSeen;

    /// <summary>
    /// İç katmandaki bir hata işleyicinin (UseExceptionHandler, geliştirici sayfası) yakaladığı
    /// istisna. Yanıt 5xx ile biterse raporlanır; işleyici onu 4xx'e çevirdiyse atılır.
    /// </summary>
    public Exception? PendingException;

    /// <summary>İstisnanın düştüğü anki rota — hata sayfası yeniden yürütülmeden önce.</summary>
    public string? PendingRoute;

    public void RecordQuery(string sql, double durationMs)
    {
        lock (_gate)
        {
            _queryCount++;

            if (durationMs > _slowestMs)
            {
                _slowestMs = durationMs;
                _slowestSql = sql;
            }
        }
    }

    public (int Count, double? SlowestMs, string? SlowestSql) Queries()
    {
        lock (_gate)
        {
            return (_queryCount, _slowestMs >= 0 ? _slowestMs : null, _slowestSql);
        }
    }
}
