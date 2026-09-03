# kup-report

CLI tool that reports KUP hours from your GitHub pull requests for the current month.

Each PR should contain a tag in its title or description: `[KUP:1]` or `[KUP:1.5]`
(hours as an integer or with one decimal). The tool sums the hours and prints a report.

## Usage

```
dotnet run --project src\KupReport
```

1. Shows a welcome screen and checks cached authentication for GitHub and Azure
   DevOps. Anything missing is offered in a menu: *Authenticate with GitHub*,
   *Authenticate with Azure DevOps* (asks the organization once, then remembers
   it), *Continue without Azure DevOps*, or *Exit*. Both use browser device-code
   flows — no PATs, no environment variables. GitHub tokens and ADO refresh
   tokens are cached in `%APPDATA%\kup-report\token.json`, so subsequent runs
   sign in silently.
2. Once authenticated, a menu offers *Calculate report*, *Sign out* (GitHub,
   Azure DevOps, or everything — clears the cached tokens and returns to the
   authentication menu), or *Exit*.
3. Calculating prompts for an email (always defaults to the one attached to your
   GitHub account — enter someone else's email to generate their
   report), the number of working days in the current month (shows both the
   calendar weekday count and the HR calendar value, defaulting to HR,
   overridable), vacation days taken, the target KUP percentage (default 70%),
   and "days before" — how many trailing days of the previous month to include
   (default 0), since KUP is usually reported a few days before month end.
4. Resolves the email to a GitHub login and an Azure DevOps identity, fetches
   all matching PRs (GitHub: created in the period; Azure DevOps: completed
   since the period start) and renders a table with the `[KUP:x]` hours, a
   summary (target vs reported hours) and a coverage chart. Open and closed
   unmerged GitHub PRs remain visible in the table, but only merged PRs count
   toward reported hours and appear in the PDF.
5. After the report, a menu offers *Start over* (recalculate with the previous
   inputs as defaults — handy after adjusting PR hours), *Export as PDF*, or
   *Exit*. The PDF replicates the original LaTeX template: Accuris logo, CMU
   Serif (Computer Modern) fonts, exact layout and colors. When Azure DevOps is
   connected, each PR is linked to its Azure Boards work item (GitHub PRs via
   `AB#123` references, ADO PRs via linked work items): the work title column
   shows `{Type} {Id}: {Title}` and the person commissioning column shows the
   work item's owner. Author/controller names and titles are prefilled from
   Microsoft Graph for whoever the report is for (reusing the ADO login — no
   extra sign-in) and shown as editable prompts before export. Nothing besides
   tokens is persisted between runs.

## Project structure

```
kup-aleh.slnx
src/KupReport/
  Program.cs            entry point / flow orchestration
  Authentication/       device-flow logins (GitHub, Entra ID) + credential cache
  GitHub/               GitHub REST API client + DTOs (source-generated JSON)
  AzureDevOps/          Azure DevOps REST API client + DTOs
  Reporting/            KUP tag parsing, report model, HR + weekday calendars
  Export/               QuestPDF works-registration report exporter
  Assets/               embedded logo + CMU Serif fonts (SIL OFL)
  Cli/                  Spectre.Console UI: welcome, prompts, report rendering
```

By default the GitHub CLI public OAuth client id is used for the GitHub device
flow (override with `KUP_GITHUB_CLIENT_ID`) and the Azure CLI public client id
for the Entra ID device flow (override with `KUP_ADO_CLIENT_ID`).

## Native AOT publish

```
dotnet publish src\KupReport -c Release -r win-x64
```

Produces a single self-contained native executable `kup.exe` in
`src\KupReport\bin\Release\net10.0\win-x64\publish\`. QuestPDF's native
libraries are embedded in the executable and extracted on first use to
`%LOCALAPPDATA%\kup-report\native\<version>\`.
