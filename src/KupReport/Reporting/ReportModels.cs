namespace KupReport.Reporting;

/// <summary>People named on the works-registration report.</summary>
public sealed record ReportIdentity(
    string AuthorName,
    string AuthorTitle,
    string ManagerName,
    string ManagerTitle);

/// <summary>Person commissioning a work (the owner of its Azure Boards work item).</summary>
public sealed record WorkOwner(string Name, string? Email);

public sealed record PullRequestEntry(
    DateTimeOffset CreatedAt,
    string Repository,
    int Number,
    string Title,
    string Url,
    string State,
    decimal? KupHours,
    WorkItemInfo? WorkItem)
{
    public bool IsMerged => State.Equals("merged", StringComparison.OrdinalIgnoreCase);

    public WorkOwner? Owner =>
        WorkItem?.OwnerName is { Length: > 0 } name ? new WorkOwner(name, WorkItem.OwnerEmail) : null;
}

public sealed record MonthlyKupReport(
    string User,
    DateOnly From,
    DateOnly To,
    int WorkingDays,
    int VacationDays,
    int TargetPercent,
    IReadOnlyList<PullRequestEntry> Entries)
{
    public decimal TotalKupHours => Entries.Where(e => e.IsMerged).Sum(e => e.KupHours ?? 0m);
    public int UntaggedCount => Entries.Count(e => e.IsMerged && e.KupHours is null);
    public int AvailableHours => (WorkingDays - VacationDays) * 8;
    public decimal TargetHours => AvailableHours * TargetPercent / 100m;
    public decimal ReportedPercent => AvailableHours > 0 ? TotalKupHours / AvailableHours * 100m : 0m;
    public decimal RemainingHours => TargetHours - TotalKupHours;

    /// <summary>Merged PRs with a [KUP:x] tag - the works listed in the PDF.</summary>
    public IEnumerable<PullRequestEntry> ReportedEntries => Entries.Where(e => e.IsMerged && e.KupHours is not null);

    /// <summary>
    /// Owner of the most distinct work items among the reported works (ties go to
    /// more PRs, then name). Used as the commissioning person for works whose work
    /// item has no owner or that aren't linked to a work item; null when none has one.
    /// </summary>
    public WorkOwner? DefaultOwner => ReportedEntries
        .Where(e => e.Owner is not null)
        .GroupBy(e => e.Owner!.Email is { Length: > 0 } email ? email : e.Owner!.Name, StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(g => g.Select(e => e.WorkItem!.Url).Distinct(StringComparer.OrdinalIgnoreCase).Count())
        .ThenByDescending(g => g.Count())
        .ThenBy(g => g.First().Owner!.Name, StringComparer.OrdinalIgnoreCase)
        .Select(g => g.First().Owner)
        .FirstOrDefault();
}
