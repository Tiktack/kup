namespace KupReport.Reporting;

public enum PullRequestSource
{
    GitHub,
    AzureDevOps,
}

/// <summary>Azure Boards work item linked to a pull request.</summary>
public sealed record WorkItemInfo(
    string Label,
    string Title,
    string Url,
    string? OwnerName,
    string? OwnerEmail);

/// <summary>Source-agnostic pull request data collected from GitHub or Azure DevOps.</summary>
public sealed record CollectedPullRequest(
    PullRequestSource Source,
    DateTimeOffset CreatedAt,
    string Repository,
    int Number,
    string Title,
    string? Body,
    string Url,
    string State)
{
    public WorkItemInfo? WorkItem { get; init; }
}
