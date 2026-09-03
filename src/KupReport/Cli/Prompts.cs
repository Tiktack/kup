using KupReport.Reporting;
using Spectre.Console;

namespace KupReport.Cli;

public sealed record ReportInput(string Email, int WorkingDays, int VacationDays, int TargetPercent, int DaysBefore);

public static class Prompts
{
    /// <summary>Menu shown while one of the providers is not authenticated.</summary>
    public static AuthMenuAction AskAuthAction(string? githubLogin, string? adoStatus)
    {
        AnsiConsole.MarkupLine(githubLogin is null
            ? "GitHub        [red]not authenticated[/]"
            : $"GitHub        [green]{Markup.Escape(githubLogin)}[/]");
        AnsiConsole.MarkupLine(adoStatus is null
            ? "Azure DevOps  [red]not authenticated[/]"
            : $"Azure DevOps  [green]{Markup.Escape(adoStatus)}[/]");
        AnsiConsole.WriteLine();

        var choices = new List<string>();
        if (githubLogin is null)
            choices.Add("Authenticate with GitHub");
        if (adoStatus is null)
        {
            choices.Add("Authenticate with Azure DevOps");
            if (githubLogin is not null)
                choices.Add("Continue without Azure DevOps");
        }
        choices.Add("Exit");

        return AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("What would you like to do?")
                .AddChoices(choices)) switch
        {
            "Authenticate with GitHub" => AuthMenuAction.AuthenticateGitHub,
            "Authenticate with Azure DevOps" => AuthMenuAction.AuthenticateAdo,
            "Continue without Azure DevOps" => AuthMenuAction.ContinueWithoutAdo,
            _ => AuthMenuAction.Exit,
        };
    }

    /// <summary>Asks for the Azure DevOps organization name.</summary>
    public static string AskAdoOrganization(string? defaultOrg)
    {
        var prompt = new TextPrompt<string>("Azure DevOps organization ([grey]dev.azure.com/<org>[/]):");
        if (defaultOrg is not null)
            prompt.DefaultValue(defaultOrg);
        return AnsiConsole.Prompt(prompt).Trim().TrimEnd('/').Split('/').Last();
    }

    /// <summary>Main menu shown after authentication.</summary>
    public static MainMenuAction AskMainMenu()
    {
        return AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("What would you like to do?")
                .AddChoices("Calculate report", "Sign out", "Exit")) switch
        {
            "Calculate report" => MainMenuAction.CalculateReport,
            "Sign out" => MainMenuAction.SignOut,
            _ => MainMenuAction.Exit,
        };
    }

    /// <summary>Asks which provider to sign out of.</summary>
    public static SignOutTarget AskSignOutTarget()
    {
        return AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Sign out of:")
                .AddChoices("GitHub", "Azure DevOps", "Everything", "Cancel")) switch
        {
            "GitHub" => SignOutTarget.GitHub,
            "Azure DevOps" => SignOutTarget.AzureDevOps,
            "Everything" => SignOutTarget.Everything,
            _ => SignOutTarget.Cancel,
        };
    }

    /// <summary>Menu shown after a report has been rendered.</summary>
    public static PostReportAction AskPostReportAction()
    {
        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("What next?")
                .AddChoices("Start over", "Export as PDF", "Exit"));

        return choice switch
        {
            "Start over" => PostReportAction.StartOver,
            "Export as PDF" => PostReportAction.ExportPdf,
            _ => PostReportAction.Exit,
        };
    }

    public static ReportInput AskReportInput(
        string? defaultEmail, string monthName, int calendarWeekdays, int? hrWorkingDays, ReportInput? previous)
    {
        var emailPrompt = new TextPrompt<string>("Email:")
            .Validate(email => email.Contains('@')
                ? ValidationResult.Success()
                : ValidationResult.Error("[red]Enter a valid email address[/]"));
        // The default is always the logged-in person's email, even on start-over.
        if (defaultEmail is { Length: > 0 })
            emailPrompt.DefaultValue(defaultEmail);

        var email = AnsiConsole.Prompt(emailPrompt).Trim();

        AnsiConsole.MarkupLine(
            $"[grey]Working days in {Markup.Escape(monthName)}: calendar [/][bold]{calendarWeekdays}[/][grey], HR [/]" +
            (hrWorkingDays is { } hr ? $"[bold]{hr}[/]" : "[red]n/a[/]"));

        var workingDays = AnsiConsole.Prompt(
            new TextPrompt<int>("Working days (override if needed):")
                .DefaultValue(previous?.WorkingDays ?? hrWorkingDays ?? calendarWeekdays)
                .Validate(days => days is >= 1 and <= 31
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]Must be between 1 and 31[/]")));

        var vacationDays = AnsiConsole.Prompt(
            new TextPrompt<int>("Vacation days taken this month:")
                .DefaultValue(previous?.VacationDays ?? 0)
                .Validate(days => days >= 0 && days <= workingDays
                    ? ValidationResult.Success()
                    : ValidationResult.Error($"[red]Must be between 0 and {workingDays}[/]")));

        var targetPercent = AnsiConsole.Prompt(
            new TextPrompt<int>("Target KUP percentage:")
                .DefaultValue(previous?.TargetPercent ?? 70)
                .Validate(percent => percent is >= 1 and <= 100
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]Must be between 1 and 100[/]")));

        var daysBefore = AnsiConsole.Prompt(
            new TextPrompt<int>("Days before (include last N days of the previous month):")
                .DefaultValue(previous?.DaysBefore ?? 0)
                .Validate(days => days is >= 0 and <= 10
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]Must be between 0 and 10[/]")));

        return new ReportInput(email, workingDays, vacationDays, targetPercent, daysBefore);
    }

    /// <summary>Asks for the people named on the PDF report, with editable defaults.</summary>
    public static ReportIdentity AskReportIdentity(
        string? authorName, string? authorTitle, string? managerName, string? managerTitle)
    {
        return new ReportIdentity(
            Ask("Author's full name:", authorName),
            Ask("Author's job title:", authorTitle),
            Ask("Controller's full name:", managerName),
            Ask("Controller's job title:", managerTitle));

        static string Ask(string label, string? defaultValue)
        {
            var prompt = new TextPrompt<string>(label).AllowEmpty();
            if (defaultValue is { Length: > 0 })
                prompt.DefaultValue(defaultValue);
            return AnsiConsole.Prompt(prompt).Trim();
        }
    }

    /// <summary>Asks for the PDF output file path.</summary>
    public static string AskPdfPath(string defaultFileName) =>
        AnsiConsole.Prompt(
            new TextPrompt<string>("Output file:")
                .DefaultValue(defaultFileName))
            .Trim();
}

public enum AuthMenuAction
{
    AuthenticateGitHub,
    AuthenticateAdo,
    ContinueWithoutAdo,
    Exit,
}

public enum MainMenuAction
{
    CalculateReport,
    SignOut,
    Exit,
}

public enum SignOutTarget
{
    GitHub,
    AzureDevOps,
    Everything,
    Cancel,
}

public enum PostReportAction
{
    StartOver,
    ExportPdf,
    Exit,
}
