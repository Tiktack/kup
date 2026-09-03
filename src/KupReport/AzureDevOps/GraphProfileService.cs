using System.Text.Json;
using System.Text.Json.Serialization;
using KupReport.Authentication;

namespace KupReport.AzureDevOps;

public sealed record PersonInfo(string Name, string Title);

public sealed class GraphUser
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("jobTitle")] public string? JobTitle { get; set; }
}

public sealed class GraphUserList
{
    [JsonPropertyName("value")] public List<GraphUser> Value { get; set; } = [];
}

[JsonSerializable(typeof(GraphUser))]
[JsonSerializable(typeof(GraphUserList))]
public sealed partial class GraphJsonContext : JsonSerializerContext;

/// <summary>
/// Reads the signed-in user's profile and manager from Microsoft Graph by
/// redeeming the cached Entra ID refresh token for a Graph access token —
/// no separate login required.
/// </summary>
public sealed class GraphProfileService(AdoApiClient api)
{
    private const string GraphScope = "https://graph.microsoft.com/.default offline_access openid profile";

    /// <summary>
    /// Returns (author, manager) for the given email, or for the signed-in user
    /// when email is null; either part may be null when unavailable.
    /// </summary>
    public async Task<(PersonInfo? Author, PersonInfo? Manager)> TryGetProfileAsync(
        string? email, CancellationToken ct)
    {
        try
        {
            var refreshToken = CredentialStore.Load().AdoRefreshToken;
            if (refreshToken is null)
                return (null, null);

            var token = await api.RedeemRefreshTokenAsync(refreshToken, GraphScope, ct);
            if (token?.AccessToken is not { Length: > 0 } accessToken)
                return (null, null);

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);

            var baseUrl = email is null
                ? "https://graph.microsoft.com/v1.0/me"
                : $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(email)}";

            var author = await GetUserAsync(http, $"{baseUrl}?$select=id,displayName,jobTitle", ct);

            // The email may not be the UPN; fall back to a mail filter.
            if (author is null && email is not null)
            {
                author = await FindByMailAsync(http, email, ct);
                if (author is not null)
                    baseUrl = $"https://graph.microsoft.com/v1.0/users/{author.Id}";
            }

            var manager = author is null
                ? null
                : await GetUserAsync(http, $"{baseUrl}/manager?$select=id,displayName,jobTitle", ct);

            return (ToPerson(author), ToPerson(manager));
        }
        catch
        {
            // Profile data is a convenience; fall back to manual entry.
            return (null, null);
        }
    }

    private static async Task<GraphUser?> GetUserAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        var user = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(ct), GraphJsonContext.Default.GraphUser);
        return user is { DisplayName.Length: > 0 } ? user : null;
    }

    private static async Task<GraphUser?> FindByMailAsync(HttpClient http, string email, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"mail eq '{email.Replace("'", "''")}'");
        using var response = await http.GetAsync(
            $"https://graph.microsoft.com/v1.0/users?$filter={filter}&$select=id,displayName,jobTitle&$top=1", ct);
        if (!response.IsSuccessStatusCode)
            return null;

        var users = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(ct), GraphJsonContext.Default.GraphUserList);
        return users?.Value.FirstOrDefault();
    }

    private static PersonInfo? ToPerson(GraphUser? user) =>
        user is null ? null : new PersonInfo(user.DisplayName, user.JobTitle ?? "");
}
