using System.Security.Cryptography;

namespace MedMissionBridge;

public enum ApiKeySource { Configured, Generated }

public sealed record ApiKeyResolution(string Key, ApiKeySource Source);

/// <summary>
/// Decides the API key the bridge runs with. Shipping with the placeholder key left
/// in <c>appsettings.json</c> is the most likely field mistake — the warning at
/// startup only helps someone reading the console — so a placeholder is replaced by
/// a generated key that persists in the data directory. The operator then reads the
/// key off the management page and types it into the tablets.
/// </summary>
public static class ApiKeyBootstrap
{
    public const string DefaultKey = "changeme-dev-key";
    public const string FileName = "api-key.txt";

    /// <summary>Ambiguous characters are out: 0/O and 1/I/L get mistyped from a screen.</summary>
    private const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
    private const int GroupCount = 4;
    private const int GroupSize = 5;

    public static bool IsPlaceholder(string? key) =>
        string.IsNullOrWhiteSpace(key) || key == DefaultKey;

    public static string Generate()
    {
        var groups = new string[GroupCount];
        for (var g = 0; g < GroupCount; g++)
        {
            var chars = new char[GroupSize];
            for (var i = 0; i < GroupSize; i++)
                chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            groups[g] = new string(chars);
        }
        return string.Join('-', groups);
    }

    /// <param name="dataDir">Called only when a key has to be read or written, so a
    /// properly configured deployment never needs the directory to exist.</param>
    public static ApiKeyResolution Resolve(string? configuredKey, Func<string> dataDir)
    {
        if (!IsPlaceholder(configuredKey))
            return new ApiKeyResolution(configuredKey!, ApiKeySource.Configured);

        var path = Path.Combine(dataDir(), FileName);
        if (File.Exists(path))
        {
            var stored = File.ReadAllText(path).Trim();
            // A blank file is a half-finished write or a hand-edit, not a key.
            if (stored.Length > 0) return new ApiKeyResolution(stored, ApiKeySource.Generated);
        }

        var generated = Generate();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, generated);
        return new ApiKeyResolution(generated, ApiKeySource.Generated);
    }
}
