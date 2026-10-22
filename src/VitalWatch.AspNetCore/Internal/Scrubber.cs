using System.Text.RegularExpressions;

namespace VitalWatch.Internal;

/// <summary>
/// Kişisel veri temizliği — gönderimden ÖNCE uygulanır.
/// </summary>
/// <remarks>
/// Hub tarafında aynı temizlik tekrar yapılır. İkisi de gereklidir: hub kendini savunur,
/// paket ise kişisel veriyi ağa hiç çıkarmaz.
///
/// Desenler kardeş paketlerle (<c>cestnytr/vitalwatch-laravel</c>, <c>vitalwatch-node</c>,
/// <c>vitalwatch</c> Python) birebir aynı davranmak zorundadır: ayrışırlarsa aynı hata iki
/// SDK'da farklı maskelenir, parmak izi bölünür ve panelde tek hata iki satır görünür.
/// Ortak test vektörleri <c>tests/.../Fixtures/mask-vectors.json</c> içinde.
///
/// .NET'e özgü dikkat: .NET düzenli ifadelerinde <c>\b</c>, <c>\d</c> ve <c>\s</c> Unicode
/// farkındadır; PHP'de (<c>/u</c> bayraksız desenler) ve JS'de ASCII'dir. Birebir aynı
/// davranış için bu üçü açık ASCII sınıflarıyla yazıldı — örn. <c>ş12345678901</c> PHP'de
/// TC no olarak maskelenir, <c>\b</c> kullanılsaydı burada maskelenmezdi.
/// </remarks>
internal static class Scrubber
{
    // ASCII sözcük sınırı: PHP/JS'deki \b'nin rakam ve harf kenarındaki karşılığı.
    private const string Before = "(?<![A-Za-z0-9_])";
    private const string After = "(?![A-Za-z0-9_])";

    // PHP'nin /u bayraksız \s kümesi.
    private const string Space = @"[\t\n\x0B\f\r ]";

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private const RegexOptions Options = RegexOptions.CultureInvariant;

    private static readonly Regex Email = new(@"[\p{L}\p{N}._%+-]+@[\p{L}\p{N}.-]+\.\p{L}{2,}", Options, MatchTimeout);

    private static readonly Regex Iban = new(Before + $"[Tt][Rr](?:[\\t\\n\\x0B\\f\\r -]?[0-9]){{24}}" + After, Options, MatchTimeout);

    // Aday; karar IsCard'da — dosya adındaki zaman damgası kart sanılmasın.
    private static readonly Regex Card = new(Before + "[0-9](?:[\\t\\n\\x0B\\f\\r -]?[0-9]){12,18}" + After, Options, MatchTimeout);

    private static readonly Regex NationalId = new(Before + "[1-9][0-9]{10}" + After, Options, MatchTimeout);

    // Önünde rakam olamaz: `1795123456789` damgası `179[phone]` oluyordu.
    private static readonly Regex Phone = new(
        "(?<![0-9])(?:\\+?90|0)?[\\t\\n\\x0B\\f\\r -]?5[0-9]{2}[\\t\\n\\x0B\\f\\r -]?[0-9]{3}[\\t\\n\\x0B\\f\\r -]?[0-9]{2}[\\t\\n\\x0B\\f\\r -]?[0-9]{2}" + After,
        Options,
        MatchTimeout);

    /*
     * Uzun rastgele diziler: oturum kimliği, API anahtarı, jeton, hash. Kardeş pakette
     * gerçek bir sızıntıda yakalandı — veritabanı hatasının mesajı SQL'i bağlanmış
     * değerlerle taşıyordu ve içinde oturum kimliği vardı. 24 hane eşiği bilinçli.
     * Aday; karar IsToken'da — okunur dosya adları maskelenmesin.
     */
    private static readonly Regex Token = new("[A-Za-z0-9][A-Za-z0-9_-]{23,}", Options, MatchTimeout);

    // Düz CamelCase/camelCase ad: IsToken'ın ilk kontrolü. \z — `$` sondaki satır sonunu da kabul eder.
    private static readonly Regex CamelCase = new("^[A-Za-z][a-z]+(?:[A-Z][a-z]+)+\\z", Options, MatchTimeout);

    private static readonly Regex SingleQuoted = new("'(?:[^']|'')*'", Options, MatchTimeout);
    private static readonly Regex DoubleQuoted = new("\"(?:[^\"]|\"\")*\"", Options, MatchTimeout);
    private static readonly Regex Number = new(Before + "[0-9]+(?:\\.[0-9]+)?" + After, Options, MatchTimeout);
    private static readonly Regex InList = new(Before + "([Ii][Nn])" + Space + "*\\(" + Space + "*\\?(?:" + Space + "*," + Space + "*\\?)+" + Space + "*\\)", Options, MatchTimeout);
    private static readonly Regex Whitespace = new(@"\s+", Options, MatchTimeout);
    private static readonly Regex QuotedAssignment = new("\"(?:[^\"]|\"\")*\"(\\s*=\\s*)\\S+", Options, MatchTimeout);
    private static readonly Regex DashOrUnderscore = new("[-_]", Options, MatchTimeout);

    /// <summary>Maskele ve kırp. Boş değer <c>null</c> döner — hub'a boş alan gitmez.</summary>
    public static string? Text(string? value, int limit)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var s = value;

        try
        {
            s = Email.Replace(s, "[email]");
            s = Iban.Replace(s, "[iban]");
            s = Card.Replace(s, m => IsCard(m.Value) ? "[card]" : m.Value);
            s = NationalId.Replace(s, "[national_id]");
            s = Phone.Replace(s, "[phone]");
            s = Token.Replace(s, m => IsToken(m.Value) ? "[token]" : m.Value);
        }
        catch (RegexMatchTimeoutException)
        {
            /*
             * Patolojik girdi. Maskelenmemiş metni göndermek yerine hiç göndermemek:
             * sızıntı riski, eksik kayıttan pahalıdır.
             */
            return "[unscrubbable]";
        }

        return Truncate(s, limit);
    }

    /// <summary>Query string ve fragment atılır; yalnızca yol kalır.</summary>
    public static string? Path(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var path = value;

        // Tam adres: şema ve host atılır.
        var scheme = path.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 0 && path.AsSpan(0, scheme).IndexOfAny("/?#") < 0)
        {
            var slash = path.IndexOf('/', scheme + 3);
            path = slash < 0 ? "/" : path[slash..];
        }

        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            path = path[..cut];
        }

        return path.Length == 0 ? null : Text(path, 300);
    }

    /// <summary>
    /// SQL normalize: literal değerler <c>?</c> ile değiştirilir.
    /// <c>where email = 'ahmet@ornek.com'</c> → <c>where email = ?</c>
    /// </summary>
    /// <remarks>
    /// Hem KVKK gereği hem gruplama açısından doğru: aynı sorgu farklı parametrelerle
    /// çalıştığında tek parmak izinde toplanır. Çift tırnaklı tanımlayıcılar (PostgreSQL'in
    /// <c>"Products"</c>'ı) da <c>?</c> olur — kardeş paketler ve hub aynısını yapıyor;
    /// ayrışmak parmak izini bölerdi.
    /// </remarks>
    public static string? Sql(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            var s = SingleQuoted.Replace(value, "?");
            s = DoubleQuoted.Replace(s, "?");
            s = Number.Replace(s, "?");
            s = InList.Replace(s, "$1 (?)");
            s = Whitespace.Replace(s.Trim(), " ");

            return Text(s, 500);
        }
        catch (RegexMatchTimeoutException)
        {
            return "[unscrubbable]";
        }
    }

    /// <summary>
    /// Hata mesajı. Veritabanı sürücülerinin hataları SQL'i bağlanmış değerlerle taşır;
    /// önce literal değerler atılır, sonra genel maskeleme uygulanır.
    /// </summary>
    public static string? Message(string? value, bool containsSql = false)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var s = value;

        if (containsSql)
        {
            try
            {
                s = SingleQuoted.Replace(s, "?");
                s = QuotedAssignment.Replace(s, "\"?\"$1?");
            }
            catch (RegexMatchTimeoutException)
            {
                return "[unscrubbable]";
            }
        }

        return Text(s, 500);
    }

    /// <summary>Stack trace kısaltılır ve maskelenir.</summary>
    public static string? Stack(string? value) => Text(value, 2000);

    /// <summary>
    /// Gerçek kart numarası: 2-9 ile başlar ve Luhn'dan geçer. Milisaniye zaman damgası
    /// 1 ile başlar ve kart sanılmaz; her gerçek kart Luhn'u geçer.
    /// </summary>
    internal static bool IsCard(string candidate)
    {
        var digits = new List<int>(candidate.Length);
        foreach (var c in candidate)
        {
            if (c is >= '0' and <= '9')
            {
                digits.Add(c - '0');
            }
        }

        if (digits.Count == 0 || digits[0] < 2)
        {
            return false;
        }

        var sum = 0;
        var doubled = false;

        for (var i = digits.Count - 1; i >= 0; i--)
        {
            var d = digits[i];

            if (doubled)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
            doubled = !doubled;
        }

        return sum % 10 == 0;
    }

    /// <summary>
    /// Rastgele dizi: tire/alt çizgiyle bölünmemiş 16+ karakterlik parça taşır ya da harf
    /// içeren hex'tir (UUID, hash). Tireyle birleşmiş kısa parçalar okunur addır.
    /// </summary>
    internal static bool IsToken(string candidate)
    {
        // Düz CamelCase/camelCase ad jeton değildir: gerçek jeton ve oturum
        // kimlikleri neredeyse hep rakam taşır; rakamsız, yan yana iki büyük
        // harfi olmayan ve küçük harfle biten 24+ karakterlik rastgele dizinin
        // olasılığı 1/10.000'in çok altında. .NET'in `InvalidOperationException`
        // gibi adları maskeleniyor, yığın izleri okunmaz oluyordu.
        if (CamelCase.IsMatch(candidate))
        {
            return false;
        }

        foreach (var part in DashOrUnderscore.Split(candidate))
        {
            if (part.Length >= 16)
            {
                return true;
            }
        }

        var hasLetter = false;

        foreach (var c in candidate)
        {
            if (c is '-' or '_' or (>= '0' and <= '9'))
            {
                continue;
            }

            if (c is (>= 'a' and <= 'f') or (>= 'A' and <= 'F'))
            {
                hasLetter = true;
                continue;
            }

            return false;
        }

        return hasLetter;
    }

    /// <summary>UTF-16 birimiyle kırpar; vekil çiftini ortadan bölmez.</summary>
    internal static string Truncate(string value, int limit)
    {
        if (value.Length <= limit)
        {
            return value;
        }

        var end = limit;
        if (end > 0 && char.IsHighSurrogate(value[end - 1]))
        {
            end--;
        }

        return value[..end];
    }
}
