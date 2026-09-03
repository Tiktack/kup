namespace KupReport.Reporting;

public static class ReportBuilder
{
    public static MonthlyKupReport Build(
        string user, DateOnly from, DateOnly to, int workingDays, int vacationDays, int targetPercent,
        IEnumerable<CollectedPullRequest> pullRequests)
    {
        var entries = pullRequests
            .OrderBy(pr => pr.CreatedAt)
            .Select(pr => new PullRequestEntry(
                CreatedAt: pr.CreatedAt,
                Repository: pr.Repository,
                Number: pr.Number,
                Title: pr.Title,
                Url: pr.Url,
                State: pr.State,
                KupHours: KupParser.ExtractHours($"{pr.Title}\n{pr.Body}"),
                WorkItem: pr.WorkItem))
            .ToList();

        return new MonthlyKupReport(user, from, to, workingDays, vacationDays, targetPercent, entries);
    }
}
