using System.Text.Json.Serialization;

namespace KupReport.GitHub;

public sealed class DeviceCodeResponse
{
    [JsonPropertyName("device_code")] public string DeviceCode { get; set; } = "";
    [JsonPropertyName("user_code")] public string UserCode { get; set; } = "";
    [JsonPropertyName("verification_uri")] public string VerificationUri { get; set; } = "";
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("interval")] public int Interval { get; set; }
}

public sealed class TokenResponse
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
    [JsonPropertyName("interval")] public int? Interval { get; set; }
}

public sealed class GitHubUser
{
    [JsonPropertyName("login")] public string Login { get; set; } = "";
    [JsonPropertyName("email")] public string? Email { get; set; }
}

public sealed class GitHubEmail
{
    [JsonPropertyName("email")] public string Email { get; set; } = "";
    [JsonPropertyName("primary")] public bool Primary { get; set; }
}

public sealed class UserSearchResult
{
    [JsonPropertyName("items")] public List<GitHubUser> Items { get; set; } = [];
}

public sealed class SearchResult
{
    [JsonPropertyName("total_count")] public int TotalCount { get; set; }
    [JsonPropertyName("items")] public List<SearchItem> Items { get; set; } = [];
}

public sealed class SearchItem
{
    [JsonPropertyName("number")] public int Number { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
    [JsonPropertyName("repository_url")] public string RepositoryUrl { get; set; } = "";
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("pull_request")] public SearchPullRequest PullRequest { get; set; } = new();
}

public sealed class SearchPullRequest
{
    [JsonPropertyName("merged_at")] public DateTimeOffset? MergedAt { get; set; }
}

public sealed class TokenCache
{
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DeviceCodeResponse))]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(GitHubUser))]
[JsonSerializable(typeof(List<GitHubEmail>))]
[JsonSerializable(typeof(UserSearchResult))]
[JsonSerializable(typeof(SearchResult))]
[JsonSerializable(typeof(TokenCache))]
public sealed partial class GitHubJsonContext : JsonSerializerContext;
