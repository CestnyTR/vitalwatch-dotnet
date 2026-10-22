using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace VitalWatch.Internal;

/// <summary>
/// İsteğin rota DESENİ: <c>GET /api/products/{id}</c>, gerçek yol değil.
/// </summary>
/// <remarks>
/// Desen gönderilir, gerçek id değil: hem gruplama çalışır hem yolda kişisel veri taşınmaz.
/// Desen yoksa (eşleşmeyen istek) yola düşülür; query string atılır ve yol maskelenir.
/// </remarks>
internal static class RouteDescriber
{
    /// <summary>Yöntemli etiket: <c>GET /api/products/{id}</c>.</summary>
    public static string Describe(HttpContext context) => context.Request.Method + " " + Pattern(context);

    /// <summary>Yöntemsiz desen: <c>/api/products/{id}</c>.</summary>
    public static string Pattern(HttpContext context)
    {
        try
        {
            /*
             * Hata işleyici isteği hata sayfasına yeniden yürütürken uç noktayı siliyor ya da
             * değiştiriyor; o noktada GetEndpoint() "/Error" döner. Asıl uç nokta işleyicinin
             * özelliğinde saklı — panelde hatanın patladığı uç görünsün, hata sayfası değil.
             */
            var handler = context.Features.Get<IExceptionHandlerPathFeature>();
            var reExecute = context.Features.Get<IStatusCodeReExecuteFeature>();

            var endpoint = handler?.Endpoint ?? reExecute?.Endpoint ?? context.GetEndpoint();

            if (endpoint is RouteEndpoint route && route.RoutePattern.RawText is { } raw)
            {
                return Normalize(raw, context, route);
            }

            var path = handler?.Path ?? reExecute?.OriginalPath ?? context.Request.Path.Value;

            return Scrubber.Path(path) ?? "/";
        }
        catch
        {
            return "/";
        }
    }

    /// <summary>
    /// Ham deseni okunur biçime getirir. Klasik MVC rotasında
    /// (<c>{controller=Home}/{action=Index}/{id?}</c>) denetleyici ve eylem adları yerine
    /// konur: <c>/Orders/Details/{id?}</c>. Aksi halde bütün MVC uçları tek satırda toplanırdı.
    /// </summary>
    internal static string Normalize(string raw, HttpContext? context, RouteEndpoint? endpoint)
    {
        var pattern = raw;

        if (pattern.Contains('{'))
        {
            foreach (var name in new[] { "area", "controller", "action", "page" })
            {
                var value = Value(context, endpoint, name);
                if (value is null)
                {
                    continue;
                }

                pattern = ReplaceParameter(pattern, name, value);
            }
        }

        if (!pattern.StartsWith('/'))
        {
            pattern = "/" + pattern;
        }

        return Scrubber.Truncate(pattern, 300);
    }

    private static string? Value(HttpContext? context, RouteEndpoint? endpoint, string name)
    {
        var value = context?.Request.RouteValues[name]
            ?? endpoint?.RoutePattern.RequiredValues.GetValueOrDefault(name);

        var text = value?.ToString();

        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary><c>{controller=Home}</c>, <c>{controller}</c>, <c>{controller?}</c> → değer.</summary>
    private static string ReplaceParameter(string pattern, string name, string value)
    {
        var start = 0;

        while (true)
        {
            var open = pattern.IndexOf('{', start);
            if (open < 0)
            {
                return pattern;
            }

            var close = pattern.IndexOf('}', open);
            if (close < 0)
            {
                return pattern;
            }

            var inner = pattern.AsSpan(open + 1, close - open - 1);
            var end = inner.IndexOfAny("=:?");
            var parameter = end < 0 ? inner : inner[..end];

            if (parameter.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return pattern[..open] + value + pattern[(close + 1)..];
            }

            start = close + 1;
        }
    }
}
