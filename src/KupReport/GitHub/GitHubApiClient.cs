using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using KupReport.Reporting;

namespace KupReport.GitHub;

/// <summary>Thin client over the GitHub REST API.</summary>
public sealed class GitHubApiClient : IDisposable
{
    private readonly HttpClient _http;

    public GitHubApiClient()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("kup-report", "1.0"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public void Dispose() => _http.Dispose();

    public void SetToken(string token) =>
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    /// <summary>Returns the login of the user owning the given token, or null when invalid.</summary>
    public async Task<string?> GetLoginAsync(string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        var user = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(ct), GitHubJsonContext.Default.GitHubUser);
        return user?.Login;
    }

    /// <summary>Returns the authenticated user's primary email, or null when not accessible.</summary>
    public async Task<string?> GetPrimaryEmailAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync("https://api.github.com/user/emails", ct);
            if (response.IsSuccessStatusCode)
            {
                var emails = JsonSerializer.Deserialize(
                    await response.Content.ReadAsStringAsync(ct), GitHubJsonContext.Default.ListGitHubEmail);
                var primary = emails?.FirstOrDefault(e => e.Primary)?.Email ?? emails?.FirstOrDefault()?.Email;
                if (primary is { Length: > 0 })
                    return primary;
            }

            // Fall back to the public profile email.
            using var userResponse = await _http.GetAsync("https://api.github.com/user", ct);
            if (!userResponse.IsSuccessStatusCode)
                return null;
            var user = JsonSerializer.Deserialize(
                await userResponse.Content.ReadAsStringAsync(ct), GitHubJsonContext.Default.GitHubUser);
            return user?.Email;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Finds a GitHub login by email address, or null when no match.</summary>
    public async Task<string?> FindLoginByEmailAsync(string email, CancellationToken ct)
    {
        var query = Uri.EscapeDataString($"{email} in:email");
        using var response = await _http.GetAsync($"https://api.github.com/search/users?q={query}&per_page=1", ct);
        if (!response.IsSuccessStatusCode)
            return null;

        var result = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(ct), GitHubJsonContext.Default.UserSearchResult);
        return result?.Items.FirstOrDefault()?.Login;
    }

    /// <summary>Checks whether a GitHub login exists.</summary>
    public async Task<bool> UserExistsAsync(string login, CancellationToken ct)
    {
        using var response = await _http.GetAsync(
            $"https://api.github.com/users/{Uri.EscapeDataString(login)}", ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<List<CollectedPullRequest>> SearchPullRequestsAsync(
        string author, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var query = Uri.EscapeDataString($"type:pr author:{author} created:{from:yyyy-MM-dd}..{to:yyyy-MM-dd}");
        var items = new List<SearchItem>();

        for (var page = 1; page <= 10; page++)
        {
            var url = $"https://api.github.com/search/issues?q={query}&per_page=100&page={page}&advanced_search=true";
            using var response = await _http.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"GitHub search failed ({(int)response.StatusCode}): {body}");
            }

            var result = JsonSerializer.Deserialize(
                await response.Content.ReadAsStringAsync(ct), GitHubJsonContext.Default.SearchResult)
                ?? throw new InvalidOperationException("Empty search response.");

            items.AddRange(result.Items);
            if (items.Count >= result.TotalCount || result.Items.Count == 0)
                break;
        }

        return items
            .Select(i => new CollectedPullRequest(
                Source: PullRequestSource.GitHub,
                CreatedAt: i.CreatedAt,
                Repository: i.RepositoryUrl.Split("/repos/").Last(),
                Number: i.Number,
                Title: i.Title,
                Body: i.Body,
                Url: i.HtmlUrl,
                State: i.PullRequest.MergedAt is not null ? "merged" : i.State))
            .ToList();
    }

    /// <summary>Fetches a pull request's current title, body and diff size.</summary>
    public async Task<GitHubPullRequest> GetPullRequestAsync(string repository, int number, CancellationToken ct)
    {
        using var response = await _http.GetAsync(PullRequestUrl(repository, number), ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await DescribeErrorAsync(response, ct));

        return JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(ct), GitHubJsonContext.Default.GitHubPullRequest)
            ?? throw new InvalidOperationException("Empty pull request response.");
    }

    /// <summary>Updates a pull request's title and/or body; null leaves the field unchanged.</summary>
    public async Task UpdatePullRequestAsync(
        string repository, int number, string? title, string? body, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(
            new GitHubPullRequestUpdate { Title = title, Body = body },
            GitHubJsonContext.Default.GitHubPullRequestUpdate);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _http.PatchAsync(PullRequestUrl(repository, number), content, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await DescribeErrorAsync(response, ct));
    }

    private static string PullRequestUrl(string repository, int number) =>
        $"https://api.github.com/repos/{string.Join('/', repository.Split('/').Select(Uri.EscapeDataString))}/pulls/{number}";

    private static async Task<string> DescribeErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        string? message = null;
        try
        {
            message = JsonSerializer.Deserialize(body, GitHubJsonContext.Default.GitHubError)?.Message;
        }
        catch (JsonException)
        {
        }

        return $"GitHub returned {(int)response.StatusCode}: {message ?? body}";
    }

    public async Task<DeviceCodeResponse> RequestDeviceCodeAsync(string clientId, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = "repo user:email",
        });
        using var response = await _http.PostAsync("https://github.com/login/device/code", content, ct);
        response.EnsureSuccessStatusCode();

        return JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(ct), GitHubJsonContext.Default.DeviceCodeResponse)
            ?? throw new InvalidOperationException("Empty device code response.");
    }

    public async Task<TokenResponse?> ExchangeDeviceCodeAsync(
        string clientId, string deviceCode, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["device_code"] = deviceCode,
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
        });
        using var response = await _http.PostAsync("https://github.com/login/oauth/access_token", content, ct);
        return JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(ct), GitHubJsonContext.Default.TokenResponse);
    }
}
