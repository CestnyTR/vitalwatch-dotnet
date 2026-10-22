using System.Collections.Concurrent;
using System.Reflection;

namespace VitalWatch.Internal;

/// <summary>
/// Tanılama yükünden (DiagnosticListener payload) özelliği adla okur.
/// </summary>
/// <remarks>
/// EF Core, SqlClient ve ASP.NET Core yüklerinin türlerine derleme zamanında
/// erişilemiyor: paket bu kütüphanelere bağımlı değil ve olmamalı. <c>dynamic</c>
/// kullanılmıyor (Microsoft.CSharp bağımlılığı ve her çağrıda bağlama maliyeti); tür ve ad
/// başına bir kez <see cref="PropertyInfo"/> bulunup önbelleğe alınıyor. Ad büyük/küçük harfe
/// duyarsız: ASP.NET Core anonim nesnede <c>httpContext</c>, yeni sürümlerde tipli yükte
/// <c>HttpContext</c> kullanıyor.
/// </remarks>
internal static class PropertyFetcher
{
    private static readonly ConcurrentDictionary<(Type, string), PropertyInfo?> Cache = new();

    public static object? Fetch(object? payload, string name)
    {
        if (payload is null)
        {
            return null;
        }

        try
        {
            var property = Cache.GetOrAdd(
                (payload.GetType(), name),
                static key => key.Item1.GetProperty(
                    key.Item2,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase));

            return property?.GetValue(payload);
        }
        catch
        {
            return null;
        }
    }
}
