using System.Globalization;
using KupReport.Reporting;
using Spectre.Console;

namespace KupReport.Cli;

public static class ReportRenderer
{
    public static void Render(MonthlyKupReport report)
    {
        AnsiConsole.WriteLine();

        if (report.Entries.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No pull requests found for this month.[/]");
            RenderSummary(report);
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Title($"[bold]PRs by {Markup.Escape(report.User)} - {report.From:yyyy-MM-dd} .. {report.To:yyyy-MM-dd}[/]")
            .AddColumn("Date")
            .AddColumn(new TableColumn("Hours").RightAligned())
            .AddColumn("State")
            .AddColumn("Pull request");

        foreach (var entry in report.Entries)
        {
            var hours = entry.KupHours is { } h
                ? entry.IsMerged ? $"[green]{Hours(h)}[/]" : $"[grey]{Hours(h)}[/]"
                : "[red]-[/]";

            var state = entry.State switch
            {
                "merged" => "[green]merged[/]",
                "open" => "[yellow]open[/]",
                "closed" => "[purple]closed[/]",
                _ => Markup.Escape(entry.State),
            };

            table.AddRow(
                entry.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd"),
                hours,
                state,
                $"[bold]{Markup.Escape(entry.Repository)}#{entry.Number}[/] {Markup.Escape(entry.Title)}\n[grey][link]{Markup.Escape(entry.Url)}[/][/]");
        }

        AnsiConsole.Write(table);
        RenderSummary(report);
    }

    private static void RenderSummary(MonthlyKupReport report)
    {
        var over = report.RemainingHours < 0;

        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[grey]Pull requests[/]", report.Entries.Count.ToString());
        grid.AddRow("[grey]Working days[/]", (report.WorkingDays - report.VacationDays).ToString());
        grid.AddRow("[grey]Vacation days[/]", report.VacationDays.ToString());
        grid.AddRow("[grey]Available hours[/]", $"{report.AvailableHours}h");
        grid.AddRow("[grey]Target hours[/]", $"{Hours(report.TargetHours)}h ({report.TargetPercent}%)");
        grid.AddRow("[grey]Reported KUP hours[/]",
            $"[bold {(over ? "red" : "green")}]{Hours(report.TotalKupHours)}h ({Hours(report.ReportedPercent)}%)[/]");
        grid.AddRow(
            over ? "[grey]Overreported[/]" : "[grey]Remaining[/]",
            over
                ? $"[red]{Hours(-report.RemainingHours)}h[/]"
                : $"[yellow]{Hours(report.RemainingHours)}h[/]");

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(grid).Header(" Summary ").BorderColor(Color.DodgerBlue1).Padding(2, 1));

        if (report.TargetHours > 0)
        {
            var covered = Math.Clamp((double)report.TotalKupHours, 0, (double)report.TargetHours);
            AnsiConsole.Write(new BreakdownChart()
                .Width(60)
                .AddItem("Reported", covered, over ? Color.Red : Color.Green)
                .AddItem("Remaining", (double)report.TargetHours - covered, Color.Grey));
            AnsiConsole.WriteLine();
        }

        if (report.UntaggedCount > 0)
            AnsiConsole.MarkupLine($"[red]Warning:[/] {report.UntaggedCount} PR(s) have no [[KUP:x]] tag.");
    }

    /// <summary>Shows the proposed [KUP:x] tags before they are written to GitHub.</summary>
    public static void RenderAutoTagPlan(AutoTagPlan plan, MonthlyKupReport report)
    {
        AnsiConsole.WriteLine();

        var mode = plan.Mode == AutoTagMode.FillRemaining ? "fill remaining hours" : "full recalculation";
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Title($"[bold]Proposed KUP hours - {mode}[/]")
            .AddColumn("Date")
            .AddColumn(new TableColumn("Lines").RightAligned())
            .AddColumn(new TableColumn("Files").RightAligned())
            .AddColumn(new TableColumn("Current").RightAligned())
            .AddColumn(new TableColumn("New").RightAligned())
            .AddColumn("Pull request");

        foreach (var assignment in plan.Assignments)
        {
            var pr = assignment.Candidate.PullRequest;
            var stats = assignment.Candidate.Stats;
            var current = assignment.CurrentHours is { } c ? Hours(c) : "-";
            var proposed = assignment.NewHours is { } n ? Hours(n) : "-";

            table.AddRow(
                pr.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd"),
                $"[green]+{stats.Additions}[/] [red]-{stats.Deletions}[/]",
                stats.ChangedFiles.ToString(CultureInfo.InvariantCulture),
                $"[grey]{current}[/]",
                assignment.HasChanges ? $"[bold yellow]{proposed}[/]" : proposed,
                $"[bold]{Markup.Escape(pr.Repository)}#{pr.Number}[/] {Markup.Escape(assignment.Candidate.Title)}");
        }

        AnsiConsole.Write(table);

        var resultingPercent = report.AvailableHours > 0 ? plan.ResultingHours / report.AvailableHours * 100m : 0m;
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[grey]Target hours[/]", $"{Hours(plan.TargetHours)}h ({report.TargetPercent}%)");
        grid.AddRow("[grey]Kept on other PRs[/]", $"{Hours(plan.KeptHours)}h");
        grid.AddRow("[grey]To distribute[/]", $"{Hours(plan.Budget)}h");
        grid.AddRow("[grey]Assigned[/]", $"[bold]{Hours(plan.AssignedHours)}h[/] across {plan.Assignments.Count - plan.WithoutTagCount} PR(s)");
        grid.AddRow("[grey]Resulting KUP hours[/]", $"[bold green]{Hours(plan.ResultingHours)}h ({Hours(resultingPercent)}%)[/]");
        grid.AddRow("[grey]PRs to update[/]", plan.Changes.Count.ToString(CultureInfo.InvariantCulture));

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(grid).Header(" Auto-tag ").BorderColor(Color.DodgerBlue1).Padding(2, 1));
        AnsiConsole.MarkupLine(
            $"[grey]Hours are split by size: √(additions + ½·deletions) + 3·√(files), " +
            $"in {Hours(KupAllocator.Step)}h steps, at least {Hours(KupAllocator.Step)}h per tagged PR.[/]");

        if (plan.Budget < KupAllocator.Step)
            AnsiConsole.MarkupLine("[yellow]Target already reached - there are no hours left to distribute.[/]");
        else if (plan.WithoutTagCount > 0)
            AnsiConsole.MarkupLine(
                $"[yellow]Not enough hours to give every PR {Hours(KupAllocator.Step)}h: " +
                $"{plan.WithoutTagCount} smallest PR(s) get no tag.[/]");
    }

    private static string Hours(decimal value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
