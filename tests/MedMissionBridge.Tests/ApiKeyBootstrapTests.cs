namespace MedMissionBridge.Tests;

public class ApiKeyBootstrapTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"bridge-key-{Guid.NewGuid():N}");

    private string KeyFile => Path.Combine(_dir, ApiKeyBootstrap.FileName);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void a_real_configured_key_wins_and_never_touches_the_disk()
    {
        var resolved = ApiKeyBootstrap.Resolve("a-real-site-key", () => _dir);

        Assert.Equal("a-real-site-key", resolved.Key);
        Assert.Equal(ApiKeySource.Configured, resolved.Source);
        // The operator's own key must not be shadowed by a stale generated file,
        // and a configured deployment must not need a writable data directory.
        Assert.False(Directory.Exists(_dir));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(ApiKeyBootstrap.DefaultKey)]
    public void a_placeholder_key_is_replaced_by_a_generated_one(string? configured)
    {
        var resolved = ApiKeyBootstrap.Resolve(configured, () => _dir);

        Assert.Equal(ApiKeySource.Generated, resolved.Source);
        Assert.NotEqual(ApiKeyBootstrap.DefaultKey, resolved.Key);
        Assert.Equal(resolved.Key, File.ReadAllText(KeyFile).Trim());
    }

    [Fact]
    public void the_generated_key_survives_a_restart()
    {
        // Tablets are configured with this key by hand; regenerating it on every
        // start would silently break every tablet in the field after a reboot.
        var first = ApiKeyBootstrap.Resolve(ApiKeyBootstrap.DefaultKey, () => _dir);
        var second = ApiKeyBootstrap.Resolve(ApiKeyBootstrap.DefaultKey, () => _dir);

        Assert.Equal(first.Key, second.Key);
    }

    [Fact]
    public void a_blank_key_file_is_regenerated_rather_than_used()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(KeyFile, "  \r\n");

        var resolved = ApiKeyBootstrap.Resolve(null, () => _dir);

        Assert.NotEmpty(resolved.Key);
        Assert.Equal(resolved.Key, File.ReadAllText(KeyFile).Trim());
    }

    [Fact]
    public void surrounding_whitespace_in_the_key_file_is_ignored()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(KeyFile, "\r\nSTORED-KEY\r\n");

        Assert.Equal("STORED-KEY", ApiKeyBootstrap.Resolve(null, () => _dir).Key);
    }

    [Fact]
    public void generated_keys_are_unique_and_typeable_on_a_tablet()
    {
        var keys = Enumerable.Range(0, 50).Select(_ => ApiKeyBootstrap.Generate()).ToList();

        Assert.Equal(50, keys.Distinct().Count());
        foreach (var key in keys)
        {
            // Four groups of five keeps it readable off a laptop screen, and the
            // alphabet drops the character pairs people mistype (0/O, 1/I/L).
            Assert.Matches("^[A-Z2-9]{5}(-[A-Z2-9]{5}){3}$", key);
            Assert.DoesNotContain(key, c => c is 'I' or 'L' or 'O');
        }
    }
}
