using System.Net.Http.Headers;
using System.Text.Json;
using KupReport.Reporting;

namespace KupReport.AzureDevOps;

/// <summary>Thin client over the Azure DevOps REST API and the Entra ID token endpoints.</summary>
public sealed class AdoApiClient : IDisposable
{
    // Azure DevOps resource app id used for Entra ID scopes.
    private const string AdoResource = "499b84ac-1321-427f-aa17-267ca6975798";
    public const string Scope = $"{AdoResource}/.default offline_access openid profile";

    // Azure CLI's well-known public client id (first-party, pre-consented in every tenant).
    // Override with KUP_ADO_CLIENT_ID.
    public static string ClientId =>
        Environment.GetEnvironmentVariable("KUP_ADO_CLIENT_ID") ?? "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

    private const string TokenEndpoint = "https://login.microsoftonline.com/organizations/oauth2/v2.0/token";
    private const string DeviceCodeEndpoint = "https://login.microsoftonline.com/organizations/oauth2/v2.0/devicecode";

    private readonly HttpClient _http;

    public AdoApiClient()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("kup-report", "1.0"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public void Dispose() => _http.Dispose();

    public void SetToken(string accessToken) =>
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    public async Task<AdoDeviceCodeResponse> RequestDeviceCodeAsync(string clientId, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = Scope,
        });
        using var response = await _http.PostAsync(DeviceCodeEndpoint, content, ct);
        response.EnsureSuccessStatusCode();

        return JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(ct), AdoJsonContext.Default.AdoDeviceCodeResponse)
            ?? throw new InvalidOperationException("Empty device code response.");
    }

    public async Task<AdoTokenResponse?> ExchangeDeviceCodeAsync(string clientId, string deviceCode, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["device_code"] = deviceCode,
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
        });
        using var response = await _http.PostAsync(TokenEndpoint, content, ct);
        return JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(ct), AdoJsonContext.Default.AdoTokenResponse);
    }

    public Task<AdoTokenResponse?> RefreshTokenAsync(string clientId, string refreshToken, CancellationToken ct) =>
        RedeemRefreshTokenAsync(refreshToken, Scope, ct, clientId);

    /// <summary>Redeems a refresh token for an access token with the given scope (e.g. Microsoft Graph).</summary>
    public async Task<AdoTokenResponse?> RedeemRefreshTokenAsync(
        string refreshToken, string scope, CancellationToken ct, string? clientId = null)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId ?? ClientId,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
            ["scope"] = scope,
        });
        using var response = await _http.PostAsync(TokenEndpoint, content, ct);
        return JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(ct), AdoJsonContext.Default.AdoTokenResponse);
    }

    /// <summary>Returns the authenticated user's identity in the organization, or null when the token is invalid.</summary>
    public async Task<AuthenticatedAdoIdentity?> GetAuthenticatedUserAsync(string organization, CancellationToken ct)
    {
        using var response = await _http.GetAsync(
            $"https://dev.azure.com/{organization}/_apis/connectionData?api-version=7.1-preview.1", ct);
        if (!response.IsSuccessStatusCode)
            return null;

        var data = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(ct), AdoJsonContext.Default.ConnectionData);
        return data?.AuthenticatedUser;
    }

    /// <summary>Lists completed PRs created by the given identity, closed on or after the given date.</summary>
    public async Task<List<CollectedPullRequest>> GetPullRequestsAsync(
        string organization, string creatorId, DateOnly from, CancellationToken ct)
    {
        var results = new List<CollectedPullRequest>();
        const int pageSize = 100;

        for (var skip = 0; skip < 1000; skip += pageSize)
        {
            var url = $"https://dev.azure.com/{organization}/_apis/git/pullrequests" +
                      $"?searchCriteria.status=completed" +
                      $"&searchCriteria.creatorId={creatorId}" +
                      $"&searchCriteria.minTime={from:yyyy-MM-dd}" +
                      $"&searchCriteria.queryTimeRangeType=closed" +
                      $"&$top={pageSize}&$skip={skip}&api-version=7.1";

            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"Azure DevOps PR query failed ({(int)response.StatusCode}): {body}");
            }

            var page = JsonSerializer.Deserialize(
                await response.Content.ReadAsStringAsync(ct), AdoJsonContext.Default.AdoPullRequestList)
                ?? throw new InvalidOperationException("Empty Azure DevOps response.");

            foreach (var pr in page.Value)
            {
                var project = Uri.EscapeDataString(pr.Repository.Project.Name);
                var repo = Uri.EscapeDataString(pr.Repository.Name);
                results.Add(new CollectedPullRequest(
                    Source: PullRequestSource.AzureDevOps,
                    CreatedAt: pr.ClosedDate ?? pr.CreationDate,
                    Repository: $"{pr.Repository.Project.Name}/{pr.Repository.Name}",
                    Number: pr.PullRequestId,
                    Title: pr.Title,
                    Body: pr.Description,
                    Url: $"https://dev.azure.com/{organization}/{project}/_git/{repo}/pullrequest/{pr.PullRequestId}",
                    State: "merged"));
            }

            if (page.Value.Count < pageSize)
                break;
        }

        return results;
    }

    /// <summary>Finds an identity id in the organization by email, or null when no match.</summary>
    public async Task<string?> FindIdentityIdByEmailAsync(string organization, string email, CancellationToken ct)
    {
        // MailAddress is an exact match; General is a broader fallback.
        return await QueryIdentityAsync(organization, "MailAddress", email, ct)
            ?? await QueryIdentityAsync(organization, "General", email, ct);
    }

    private async Task<string?> QueryIdentityAsync(
        string organization, string searchFilter, string filterValue, CancellationToken ct)
    {
        var url = $"https://vssps.dev.azure.com/{organization}/_apis/identities" +
                  $"?searchFilter={searchFilter}&filterValue={Uri.EscapeDataString(filterValue)}" +
                  "&queryMembership=None&api-version=7.1";

        using var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        var identities = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(ct), AdoJsonContext.Default.AdoIdentityList);
        return identities?.Value.FirstOrDefault()?.Id;
    }

    /// <summary>Returns the id of the first work item linked to the given ADO pull request, or null.</summary>
    public async Task<int?> GetPullRequestWorkItemIdAsync(
        string organization, string project, string repository, int pullRequestId, CancellationToken ct)
    {
        var url = $"https://dev.azure.com/{organization}/{Uri.EscapeDataString(project)}/_apis/git/repositories/" +
                  $"{Uri.EscapeDataString(repository)}/pullRequests/{pullRequestId}/workitems?api-version=7.1";

        using var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        var refs = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(ct), AdoJsonContext.Default.AdoWorkItemRefList);
        return int.TryParse(refs?.Value.FirstOrDefault()?.Id, out var id) ? id : null;
    }

    /// <summary>Fetches work item details; tolerates organizations without the Custom.Owner field.</summary>
    public async Task<WorkItemInfo?> GetWorkItemAsync(string organization, int id, CancellationToken ct)
    {
        var workItem =
            await TryGetWorkItemAsync(organization, id, "System.Title,System.WorkItemType,Custom.Owner", ct)
            ?? await TryGetWorkItemAsync(organization, id, "System.Title,System.WorkItemType", ct);

        if (workItem is null)
            return null;

        return new WorkItemInfo(
            Label: $"{workItem.Fields.WorkItemType} {workItem.Id}",
            Title: workItem.Fields.Title,
            Url: $"https://dev.azure.com/{organization}/_workitems/edit/{workItem.Id}",
            OwnerName: workItem.Fields.Owner?.DisplayName,
            OwnerEmail: workItem.Fields.Owner?.UniqueName);
    }

    private async Task<AdoWorkItem?> TryGetWorkItemAsync(
        string organization, int id, string fields, CancellationToken ct)
    {
        var url = $"https://dev.azure.com/{organization}/_apis/wit/workitems/{id}?fields={fields}&api-version=7.1";
        using var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        return JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(ct), AdoJsonContext.Default.AdoWorkItem);
    }
}
