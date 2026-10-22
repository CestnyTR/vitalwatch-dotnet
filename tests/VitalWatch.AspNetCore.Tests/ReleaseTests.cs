using VitalWatch.Internal;

namespace VitalWatch.Tests;

public sealed class ReleaseTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private readonly string _root = Directory.CreateTempSubdirectory("vitalwatch-release-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static Func<string, string?> Env(params (string Key, string Value)[] values)
    {
        var map = values.ToDictionary(v => v.Key, v => v.Value);

        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Explicit_value_wins_and_is_trimmed_to_64()
    {
        var (value, source) = ReleaseResolver.Resolve("  " + new string('v', 80) + " ", "VITALWATCH_RELEASE", Env(("GIT_COMMIT", Sha)), _root);

        Assert.Equal(new string('v', 64), value);
        Assert.Equal("VITALWATCH_RELEASE", source);
    }

    [Fact]
    public void Ci_variable_sha_is_shortened_and_lowercased()
    {
        var (value, source) = ReleaseResolver.Resolve(null, null, Env(("CI_COMMIT_SHA", Sha.ToUpperInvariant())), _root);

        Assert.Equal("0123456789ab", value);
        Assert.Equal("CI_COMMIT_SHA", source);
    }

    [Fact]
    public void Ci_variables_follow_sibling_order()
    {
        var (_, source) = ReleaseResolver.Resolve(null, null, Env(("CI_COMMIT_SHA", "a"), ("GIT_SHA", "v1.2.3")), _root);

        Assert.Equal("GIT_SHA", source);
    }

    [Fact]
    public void Non_sha_ci_value_is_kept_as_is()
    {
        var (value, _) = ReleaseResolver.Resolve(null, null, Env(("SOURCE_VERSION", "release-2026.10")), _root);

        Assert.Equal("release-2026.10", value);
    }

    [Fact]
    public void Git_loose_ref()
    {
        Write(".git/HEAD", "ref: refs/heads/main\n");
        Write(".git/refs/heads/main", Sha + "\n");

        Assert.Equal(("0123456789ab", ".git"), ReleaseResolver.Resolve(null, null, Env(), _root));
    }

    [Fact]
    public void Git_packed_refs()
    {
        Write(".git/HEAD", "ref: refs/heads/main\n");
        Write(".git/packed-refs", "# pack-refs with: peeled fully-peeled sorted\n" +
            "fedcba9876543210fedcba9876543210fedcba98 refs/heads/other\n" +
            Sha + " refs/heads/main\n^ffffffffffffffffffffffffffffffffffffffff\n");

        Assert.Equal("0123456789ab", ReleaseResolver.Resolve(null, null, Env(), _root).Value);
    }

    [Fact]
    public void Git_detached_head()
    {
        Write(".git/HEAD", Sha);

        Assert.Equal("0123456789ab", ReleaseResolver.Resolve(null, null, Env(), _root).Value);
    }

    [Fact]
    public void Git_file_with_gitdir_pointer()
    {
        Write("real-git/HEAD", "ref: refs/heads/main");
        Write("real-git/refs/heads/main", Sha);
        Write("app/.git", "gitdir: ../real-git\n");

        Assert.Equal("0123456789ab", ReleaseResolver.Resolve(null, null, Env(), Path.Combine(_root, "app")).Value);
    }

    [Fact]
    public void Git_in_parent_directory_is_found()
    {
        Write(".git/HEAD", Sha);
        Directory.CreateDirectory(Path.Combine(_root, "src", "App"));

        Assert.Equal("0123456789ab", ReleaseResolver.Resolve(null, null, Env(), Path.Combine(_root, "src", "App")).Value);
    }

    [Fact]
    public void Invalid_sha_and_escaping_ref_are_rejected()
    {
        Write(".git/HEAD", "ref: ../../etc/passwd");
        Assert.Equal((null, null), ReleaseResolver.Resolve(null, null, Env(), _root));

        Write(".git/HEAD", "not-a-sha");
        Assert.Null(ReleaseResolver.Resolve(null, null, Env(), _root).Value);
    }

    [Fact]
    public void Nothing_found_returns_null()
    {
        Assert.Equal((null, null), ReleaseResolver.Resolve(null, null, Env(), _root));
    }
}
