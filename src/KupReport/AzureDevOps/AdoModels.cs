using System.Text.Json.Serialization;

namespace KupReport.AzureDevOps;

public sealed class AdoDeviceCodeResponse
{
    [JsonPropertyName("device_code")] public string DeviceCode { get; set; } = "";
    [JsonPropertyName("user_code")] public string UserCode { get; set; } = "";
    [JsonPropertyName("verification_uri")] public string VerificationUri { get; set; } = "";
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("interval")] public int Interval { get; set; }
}

public sealed class AdoTokenResponse
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
}

public sealed class ConnectionData
{
    [JsonPropertyName("authenticatedUser")] public AuthenticatedAdoIdentity? AuthenticatedUser { get; set; }
}

public sealed class AuthenticatedAdoIdentity
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("providerDisplayName")] public string DisplayName { get; set; } = "";
}

public sealed class AdoPullRequestList
{
    [JsonPropertyName("value")] public List<AdoPullRequest> Value { get; set; } = [];
}

public sealed class AdoPullRequest
{
    [JsonPropertyName("pullRequestId")] public int PullRequestId { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("creationDate")] public DateTimeOffset CreationDate { get; set; }
    [JsonPropertyName("closedDate")] public DateTimeOffset? ClosedDate { get; set; }
    [JsonPropertyName("repository")] public AdoRepository Repository { get; set; } = new();
}

public sealed class AdoRepository
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("project")] public AdoProject Project { get; set; } = new();
}

public sealed class AdoProject
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public sealed class AdoWorkItemRefList
{
    [JsonPropertyName("value")] public List<AdoWorkItemRef> Value { get; set; } = [];
}

public sealed class AdoWorkItemRef
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
}

public sealed class AdoWorkItem
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("fields")] public AdoWorkItemFields Fields { get; set; } = new();
}

public sealed class AdoWorkItemFields
{
    [JsonPropertyName("System.Title")] public string Title { get; set; } = "";
    [JsonPropertyName("System.WorkItemType")] public string WorkItemType { get; set; } = "";
    [JsonPropertyName("Custom.Owner")] public AdoIdentityRef? Owner { get; set; }
}

public sealed class AdoIdentityRef
{
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("uniqueName")] public string? UniqueName { get; set; }
}

public sealed class AdoIdentityList
{
    [JsonPropertyName("value")] public List<AdoIdentity> Value { get; set; } = [];
}

public sealed class AdoIdentity
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("providerDisplayName")] public string DisplayName { get; set; } = "";
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AdoDeviceCodeResponse))]
[JsonSerializable(typeof(AdoTokenResponse))]
[JsonSerializable(typeof(ConnectionData))]
[JsonSerializable(typeof(AdoPullRequestList))]
[JsonSerializable(typeof(AdoWorkItemRefList))]
[JsonSerializable(typeof(AdoWorkItem))]
[JsonSerializable(typeof(AdoIdentityList))]
public sealed partial class AdoJsonContext : JsonSerializerContext;
