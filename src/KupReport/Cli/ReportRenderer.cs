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

    private static string Hours(decimal value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
