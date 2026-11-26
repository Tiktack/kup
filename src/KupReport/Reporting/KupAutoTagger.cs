namespace KupReport.Reporting;

public enum AutoTagMode
{
    /// <summary>Spread the remaining hours across merged PRs without a tag; existing tags stay.</summary>
    FillRemaining,

    /// <summary>Re-spread the whole target across all merged PRs, replacing existing tags.</summary>
    FullRecalculation,
}

/// <summary>Size of a pull request's diff.</summary>
public sealed record PullRequestChangeStats(int Additions, int Deletions, int ChangedFiles);

/// <summary>A GitHub PR eligible for auto-tagging, with its current title/body and diff size.</summary>
public sealed record AutoTagCandidate(
    CollectedPullRequest PullRequest,
    string Title,
    string? Body,
    PullRequestChangeStats Stats)
{
    public decimal? CurrentHours => KupParser.ExtractHours($"{Title}\n{Body}");
}

/// <summary>Hours proposed for one PR and the resulting title/body.</summary>
public sealed record AutoTagAssignment(
    AutoTagCandidate Candidate,
    double Weight,
    decimal? NewHours,
    string NewTitle,
    string? NewBody)
{
    public decimal? CurrentHours => Candidate.CurrentHours;
    public bool TitleChanged => NewTitle != Candidate.Title;
    public bool BodyChanged => NewBody != Candidate.Body;
    public bool HasChanges => TitleChanged || BodyChanged;
}

public sealed record AutoTagPlan(
    AutoTagMode Mode,
    decimal TargetHours,
    decimal KeptHours,
    IReadOnlyList<AutoTagAssignment> Assignments)
{
    /// <summary>Hours to spread: the target minus hours on PRs that are not re-tagged.</summary>
    public decimal Budget => Math.Max(0m, TargetHours - KeptHours);
    public decimal AssignedHours => Assignments.Sum(a => a.NewHours ?? 0m);
    public decimal ResultingHours => KeptHours + AssignedHours;
    public int WithoutTagCount => Assignments.Count(a => a.NewHours is null);
    public IReadOnlyList<AutoTagAssignment> Changes => Assignments.Where(a => a.HasChanges).ToList();
}

/// <summary>Plans [KUP:x] tags for merged GitHub PRs so the report reaches its target hours.</summary>
public static class KupAutoTagger
{
    /// <summary>Merged GitHub PRs; when filling, only those without a tag.</summary>
    public static bool IsCandidate(CollectedPullRequest pr, AutoTagMode mode) =>
        pr.Source == PullRequestSource.GitHub &&
        pr.State.Equals("merged", StringComparison.OrdinalIgnoreCase) &&
        (mode == AutoTagMode.FullRecalculation || KupParser.ExtractHours($"{pr.Title}\n{pr.Body}") is null);

    /// <param name="pullRequests">All PRs in the report.</param>
    /// <param name="candidates">Eligible PRs with fresh title/body and diff size.</param>
    public static AutoTagPlan Plan(
        AutoTagMode mode,
        decimal targetHours,
        IEnumerable<CollectedPullRequest> pullRequests,
        IReadOnlyList<AutoTagCandidate> candidates)
    {
        // When filling, PRs tagged since the report was fetched keep their tag.
        var allocatable = candidates
            .Where(c => mode == AutoTagMode.FullRecalculation || c.CurrentHours is null)
            .ToList();
        var allocatableKeys = allocatable.Select(c => Key(c.PullRequest)).ToHashSet();
        var freshHours = candidates.ToDictionary(c => Key(c.PullRequest), c => c.CurrentHours);

        var keptHours = pullRequests
            .Where(pr => pr.State.Equals("merged", StringComparison.OrdinalIgnoreCase))
            .Where(pr => !allocatableKeys.Contains(Key(pr)))
            .Sum(pr => (freshHours.TryGetValue(Key(pr), out var fresh)
                ? fresh
                : KupParser.ExtractHours($"{pr.Title}\n{pr.Body}")) ?? 0m);

        var weights = allocatable
            .Select(c => KupAllocator.Weight(c.Stats.Additions, c.Stats.Deletions, c.Stats.ChangedFiles))
            .ToList();
        var hours = KupAllocator.Allocate(Math.Max(0m, targetHours - keptHours), weights);

        var assignments = allocatable
            .Select((c, i) => Assign(mode, c, weights[i], hours[i] > 0 ? hours[i] : null))
            .OrderBy(a => a.Candidate.PullRequest.CreatedAt)
            .ToList();

        return new AutoTagPlan(mode, targetHours, keptHours, assignments);
    }

    private static AutoTagAssignment Assign(AutoTagMode mode, AutoTagCandidate candidate, double weight, decimal? hours)
    {
        var title = candidate.Title;
        var body = candidate.Body;

        if (mode == AutoTagMode.FillRemaining)
        {
            if (hours is { } h)
                body = KupParser.AppendTag(body, h);
        }
        else if (hours != candidate.CurrentHours)
        {
            if (KupParser.HasTag(title))
                title = KupParser.RemoveTags(title).Trim();
            if (KupParser.HasTag(body))
                body = KupParser.RemoveTags(body);
            if (hours is { } h)
                body = KupParser.AppendTag(body, h);
        }

        return new AutoTagAssignment(candidate, weight, hours, title, body);
    }

    private static (PullRequestSource, string, int) Key(CollectedPullRequest pr) =>
        (pr.Source, pr.Repository.ToLowerInvariant(), pr.Number);
}
