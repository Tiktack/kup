using System.Text.Json;
using System.Text.Json.Serialization;

namespace KupReport.Authentication;

/// <summary>Credentials persisted between runs.</summary>
public sealed class StoredCredentials
{
    [JsonPropertyName("github_token")] public string? GitHubToken { get; set; }
    [JsonPropertyName("ado_refresh_token")] public string? AdoRefreshToken { get; set; }
    [JsonPropertyName("ado_org")] public string? AdoOrganization { get; set; }

    // Legacy field from earlier versions that only stored the GitHub token.
    [JsonPropertyName("access_token")] public string? LegacyAccessToken { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StoredCredentials))]
public sealed partial class AuthJsonContext : JsonSerializerContext;

/// <summary>Persists credentials between runs.</summary>
public static class CredentialStore
{
    private static readonly string CacheDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "kup-report");
    private static readonly string TokenFile = Path.Combine(CacheDir, "token.json");

    public static StoredCredentials Load()
    {
        try
        {
            if (!File.Exists(TokenFile))
                return new StoredCredentials();

            var credentials = JsonSerializer.Deserialize(
                File.ReadAllText(TokenFile), AuthJsonContext.Default.StoredCredentials) ?? new StoredCredentials();

            if (credentials.GitHubToken is null && credentials.LegacyAccessToken is not null)
                credentials.GitHubToken = credentials.LegacyAccessToken;
            credentials.LegacyAccessToken = null;

            return credentials;
        }
        catch
        {
            return new StoredCredentials();
        }
    }

    public static void Update(Action<StoredCredentials> mutate)
    {
        try
        {
            var credentials = Load();
            mutate(credentials);
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(TokenFile,
                JsonSerializer.Serialize(credentials, AuthJsonContext.Default.StoredCredentials));
        }
        catch
        {
            // Cache is best-effort; ignore failures.
        }
    }
}
