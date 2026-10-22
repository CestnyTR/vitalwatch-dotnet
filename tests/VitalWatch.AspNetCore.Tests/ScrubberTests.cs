using System.Text.Json;
using VitalWatch.Internal;

namespace VitalWatch.Tests;

/// <summary>
/// Maskeleme bütün SDK'larda birebir aynı olmalı; ayrışırsa parmak izi bölünür.
/// Vektörler hub'ın kendi Scrubber'ından üretildi (vitalwatch-doc/apps/vitalwatch-flutter/maske-vektorleri.json).
/// </summary>
public class ScrubberTests
{
    private static readonly JsonElement Vectors = JsonDocument
        .Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mask-vectors.json")))
        .RootElement;

    public static IEnumerable<object[]> TextVectors() =>
        Vectors.GetProperty("text").EnumerateArray().Select(v => new object[]
        {
            v.GetProperty("aciklama").GetString()!,
            v.GetProperty("girdi").GetString()!,
            v.GetProperty("beklenen").GetString()!,
        });

    public static IEnumerable<object[]> PathVectors() =>
        Vectors.GetProperty("path").EnumerateArray().Select(v => new object[]
        {
            v.GetProperty("aciklama").GetString()!,
            v.GetProperty("girdi").GetString()!,
            v.GetProperty("beklenen").GetString()!,
        });

    [Theory]
    [MemberData(nameof(TextVectors))]
    public void Text_matches_shared_vectors(string description, string input, string expected)
    {
        var limit = Vectors.GetProperty("text_sinir").GetProperty("msg").GetInt32();

        Assert.True(expected == Scrubber.Text(input, limit), description);
    }

    [Theory]
    [MemberData(nameof(PathVectors))]
    public void Path_matches_shared_vectors(string description, string input, string expected)
    {
        Assert.True(expected == Scrubber.Path(input), description);
    }

    [Fact]
    public void Vector_file_is_not_empty()
    {
        Assert.True(Vectors.GetProperty("text").GetArrayLength() >= 20);
        Assert.True(Vectors.GetProperty("path").GetArrayLength() >= 5);
    }

    // .NET yığın izlerindeki uzun sınıf/metot adları [token] oluyordu (22 Ekim 2026).
    [Theory]
    [InlineData("InvalidOperationException")]
    [InlineData("HandleNonSuccessAndDebuggerNotification")]
    [InlineData("System.InvalidOperationException: boom")]
    public void Plain_camel_case_identifiers_are_not_tokens(string input)
    {
        Assert.Equal(input, Scrubber.Text(input, 500));
    }

    // Rakam, ardışık büyük harf ya da tümü büyük harf: eski kurala düşer.
    [Theory]
    [InlineData("x aB3dE5fG7hJ9kL1mN3pQ5rS7 y")]
    [InlineData("x ABCDEFGHIJKLMNOPQRSTUVWXYZ y")]
    [InlineData("x 5d41402abc4b2a76b9719d91 y")]
    [InlineData("x abcDEfghIJklmnOPqrsTUvwxYZab y")]
    public void Camel_case_exception_keeps_masking_random_strings(string input)
    {
        Assert.Equal("x [token] y", Scrubber.Text(input, 500));
    }

    [Fact]
    public void Stack_is_limited_to_2000()
    {
        Assert.Equal(2000, Scrubber.Stack(string.Concat(Enumerable.Repeat("at x ", 1000)))!.Length);
    }

    [Fact]
    public void Empty_values_are_null()
    {
        Assert.Null(Scrubber.Text("", 10));
        Assert.Null(Scrubber.Text(null, 10));
        Assert.Null(Scrubber.Sql(null));
        Assert.Null(Scrubber.Path(""));
    }

    [Fact]
    public void National_id_next_to_turkish_letter_is_masked_like_php()
    {
        // PHP/JS'de \b ASCII: "ş" sözcük karakteri sayılmaz, sınır vardır.
        Assert.Equal("ş[national_id]", Scrubber.Text("ş12345678901", 500));
    }

    [Fact]
    public void Arabic_indic_digits_are_not_treated_as_digits()
    {
        // .NET'in \d'si Unicode rakamlarını da kapsar; kardeş paketler kapsamaz.
        const string input = "١٢٣٤٥٦٧٨٩٠١";
        Assert.Equal(input, Scrubber.Text(input, 500));
    }

    [Theory]
    [InlineData("select * from users where email = 'a@b.com' and id = 42", "select * from users where email = ? and id = ?")]
    [InlineData("id IN (1, 2, 3)", "id IN (?)")]
    [InlineData("SELECT TOP(1) [p].[Id] FROM [Products] AS [p] WHERE [p].[Price] > 10.5", "SELECT TOP(?) [p].[Id] FROM [Products] AS [p] WHERE [p].[Price] > ?")]
    [InlineData("SELECT p.\"Id\" FROM \"Products\" AS p WHERE p.\"Name\" = @__name_0", "SELECT p.? FROM ? AS p WHERE p.? = @__name_0")]
    [InlineData("select  *\n  from t\twhere a = 'it''s'", "select * from t where a = ?")]
    [InlineData("update t set x = 1 where y in (?, ?, ?)", "update t set x = ? where y in (?)")]
    public void Sql_literals_are_normalized(string input, string expected)
    {
        Assert.Equal(expected, Scrubber.Sql(input));
    }

    [Fact]
    public void Sql_is_masked_after_normalizing()
    {
        // Literal dışında kalan uzun jeton da maskelenir.
        var sql = Scrubber.Sql("select * from sessions where id = ItLsnnji2VLJtiE1SjiyAEdzv6aZiuVqhFvcmRu1");
        Assert.Equal("select * from sessions where id = [token]", sql);
    }

    [Fact]
    public void Sql_message_does_not_leak_values()
    {
        var message = Scrubber.Message(
            "duplicate key value violates constraint: Key (email)=('a@b.com') exists",
            containsSql: true);

        Assert.DoesNotContain("a@b.com", message);
    }

    [Fact]
    public void Message_without_sql_keeps_quotes_but_masks()
    {
        Assert.Equal("Kullanıcı '[email]' yok", Scrubber.Message("Kullanıcı 'ahmet@ornek.com' yok"));
    }

    [Fact]
    public void Truncate_does_not_split_surrogate_pairs()
    {
        var value = new string('a', 9) + "😀";
        Assert.Equal(new string('a', 9), Scrubber.Truncate(value, 10));
    }
}
