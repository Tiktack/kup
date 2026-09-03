using Spectre.Console;

namespace KupReport.Cli;

public static class WelcomeScreen
{
    public static void Show()
    {
        AnsiConsole.Write(new FigletText("KUP Report").Color(Color.DodgerBlue1));
        AnsiConsole.Write(new Rule("[grey]GitHub PR hours for the current month[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();
    }
}
