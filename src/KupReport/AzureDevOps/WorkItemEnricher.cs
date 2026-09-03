using System.Text.RegularExpressions;
using KupReport.Reporting;

namespace KupReport.AzureDevOps;

/// <summary>
/// Resolves the Azure Boards work item behind each pull request: GitHub PRs are
/// matched through AB#123 references in the title or body, ADO PRs through their
/// linked work items.
/// </summary>
public sealed partial class WorkItemEnricher(AdoApiClient api, string organization)
{
    [GeneratedRegex(@"\bAB#(\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AbReference();

    private readonly Dictionary<int, WorkItemInfo?> _cache = [];

    public async Task<List<CollectedPullRequest>> EnrichAsync(
        IReadOnlyList<CollectedPullRequest> pullRequests, CancellationToken ct)
    {
        var enriched = new List<CollectedPullRequest>(pullRequests.Count);

        foreach (var pr in pullRequests)
        {
            var workItemId = pr.Source switch
            {
                PullRequestSource.GitHub => FindAbReference(pr),
                PullRequestSource.AzureDevOps => await FindLinkedWorkItemAsync(pr, ct),
                _ => null,
            };

            var workItem = workItemId is { } id ? await GetCachedWorkItemAsync(id, ct) : null;
            enriched.Add(pr with { WorkItem = workItem });
        }

        return enriched;
    }

    private static int? FindAbReference(CollectedPullRequest pr)
    {
        var match = AbReference().Match($"{pr.Title}\n{pr.Body}");
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    private async Task<int?> FindLinkedWorkItemAsync(CollectedPullRequest pr, CancellationToken ct)
    {
        var separator = pr.Repository.IndexOf('/');
        if (separator <= 0)
            return null;

        return await api.GetPullRequestWorkItemIdAsync(
            organization, pr.Repository[..separator], pr.Repository[(separator + 1)..], pr.Number, ct);
    }

    private async Task<WorkItemInfo?> GetCachedWorkItemAsync(int id, CancellationToken ct)
    {
        if (_cache.TryGetValue(id, out var cached))
            return cached;

        var workItem = await api.GetWorkItemAsync(organization, id, ct);
        _cache[id] = workItem;
        return workItem;
    }
}
