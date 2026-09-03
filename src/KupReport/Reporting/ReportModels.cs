namespace KupReport.Reporting;

/// <summary>People named on the works-registration report.</summary>
public sealed record ReportIdentity(
    string AuthorName,
    string AuthorTitle,
    string ManagerName,
    string ManagerTitle);

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
}
