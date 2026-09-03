using KupReport.Authentication;
using KupReport.AzureDevOps;
using KupReport.Cli;
using KupReport.Export;
using KupReport.GitHub;
using KupReport.Reporting;
using Spectre.Console;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

WelcomeScreen.Show();

try
{
    using var githubApi = new GitHubApiClient();
    using var adoApi = new AdoApiClient();
    var githubAuth = new GitHubAuthenticator(githubApi);
    var adoAuth = new AdoAuthenticator(adoApi);

    // Authentication stage: silently restore cached sessions, then offer the menu
    // until GitHub (required) and Azure DevOps (optional) are resolved.
    AuthenticatedUser? githubUser = null;
    AdoSession? adoSession = null;

    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .StartAsync("Checking authentication...", async _ =>
        {
            githubUser = await githubAuth.TryCachedLoginAsync(cts.Token);
            adoSession = await adoAuth.TryCachedLoginAsync(cts.Token);
        });

    // Session loop: auth stage -> main menu; signing out returns here.
    while (true)
    {
        var skipAdo = false;
        while (githubUser is null || (adoSession is null && !skipAdo))
        {
            var action = Prompts.AskAuthAction(
                githubUser?.Login,
                adoSession is null ? null : $"{adoSession.DisplayName} ({adoSession.Organization})");

            try
            {
                switch (action)
                {
                    case AuthMenuAction.AuthenticateGitHub:
                        githubUser = await githubAuth.LoginAsync(cts.Token);
                        break;

                    case AuthMenuAction.AuthenticateAdo:
                        var organization = Prompts.AskAdoOrganization(CredentialStore.Load().AdoOrganization);
                        adoSession = await adoAuth.LoginAsync(organization, cts.Token);
                        break;

                    case AuthMenuAction.ContinueWithoutAdo:
                        skipAdo = true;
                        break;

                    case AuthMenuAction.Exit:
                        return 0;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Authentication failed:[/] {Markup.Escape(ex.Message)}");
            }
        }

        AnsiConsole.MarkupLine($"GitHub        [bold green]{Markup.Escape(githubUser.Login)}[/]");
        AnsiConsole.MarkupLine(adoSession is null
            ? "Azure DevOps  [grey]skipped[/]"
            : $"Azure DevOps  [bold green]{Markup.Escape(adoSession.DisplayName)} ({Markup.Escape(adoSession.Organization)})[/]");
        AnsiConsole.WriteLine();

        // Main menu stage.
        switch (Prompts.AskMainMenu())
        {
            case MainMenuAction.Exit:
                return 0;

            case MainMenuAction.SignOut:
                var target = Prompts.AskSignOutTarget();
                if (target is SignOutTarget.GitHub or SignOutTarget.Everything)
                {
                    CredentialStore.Update(c => c.GitHubToken = null);
                    githubUser = null;
                }
                if (target is SignOutTarget.AzureDevOps or SignOutTarget.Everything)
                {
                    CredentialStore.Update(c => c.AdoRefreshToken = null);
                    adoSession = null;
                }
                if (target is not SignOutTarget.Cancel)
                    AnsiConsole.MarkupLine("[green]Signed out.[/] Cached tokens removed.");
                AnsiConsole.WriteLine();
                continue;

            case MainMenuAction.CalculateReport:
                break;
        }

    // Report loop: calculate, then start over / export / exit.
    ReportInput? lastInput = null;
    var graphProfiles = new Dictionary<string, (PersonInfo? Author, PersonInfo? Manager)>(StringComparer.OrdinalIgnoreCase);
    var confirmedIdentities = new Dictionary<string, ReportIdentity>(StringComparer.OrdinalIgnoreCase);
    string? defaultEmail = null;
    var resolvedLogins = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    var resolvedAdoIds = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    // Default email: always the logged-in person's (normalized: enterprise
    // accounts often report a "+alias" variant).
    var ownEmail = await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .StartAsync("Reading email from GitHub...", _ => githubApi.GetPrimaryEmailAsync(cts.Token));
    if (ownEmail is not null)
        ownEmail = EmailUtils.Normalize(ownEmail);
    defaultEmail = ownEmail;

    async Task<string?> ResolveGitHubLoginAsync(string email)
    {
        if (resolvedLogins.TryGetValue(email, out var cached))
            return cached;

        string? login;

        // The logged-in user's own email maps to their login without a lookup.
        if (EmailUtils.Same(email, ownEmail))
            login = githubUser!.Login;
        else
        {
            // Public email search (rarely works for enterprise accounts)...
            login = await githubApi.FindLoginByEmailAsync(email, cts.Token);

            // ...then derive an enterprise-managed login from our own login's pattern:
            // e.g. own jane.roe@x.com -> jane-roe_corp means
            // john.doe@x.com -> john-doe_corp.
            if (login is null && ownEmail is not null)
            {
                var suffixStart = githubUser!.Login.LastIndexOf('_');
                var ownLocal = ownEmail.Split('@')[0].Replace('.', '-');
                if (suffixStart > 0 && githubUser.Login[..suffixStart].Equals(ownLocal, StringComparison.OrdinalIgnoreCase))
                {
                    var candidate =
                        EmailUtils.Normalize(email).Split('@')[0].Replace('.', '-') +
                        githubUser.Login[suffixStart..];
                    if (await githubApi.UserExistsAsync(candidate, cts.Token))
                        login = candidate;
                }
            }
        }

        resolvedLogins[email] = login;
        return login;
    }

    async Task<string?> ResolveAdoIdentityAsync(string email)
    {
        if (resolvedAdoIds.TryGetValue(email, out var cached))
            return cached;

        // Our own email maps to the logged-in identity without a lookup.
        var id = EmailUtils.Same(email, ownEmail)
            ? adoSession!.UserId
            : await adoApi.FindIdentityIdByEmailAsync(adoSession!.Organization, EmailUtils.Normalize(email), cts.Token);

        resolvedAdoIds[email] = id;
        return id;
    }

    async Task<ReportIdentity> ResolveIdentityAsync(string email)
    {
        var isOwn = ownEmail is null || EmailUtils.Same(email, ownEmail);

        // Identity confirmed earlier in this run is reused as the default;
        // otherwise it is read from Microsoft Graph. Nothing is persisted.
        confirmedIdentities.TryGetValue(email, out var confirmed);

        if (confirmed is null && adoSession is not null && !graphProfiles.ContainsKey(email))
        {
            graphProfiles[email] = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Reading profile from Microsoft Graph...",
                    _ => new GraphProfileService(adoApi).TryGetProfileAsync(
                        isOwn ? null : EmailUtils.Normalize(email), cts.Token));
        }

        graphProfiles.TryGetValue(email, out var graph);

        var identity = Prompts.AskReportIdentity(
            confirmed?.AuthorName ?? graph.Author?.Name,
            confirmed?.AuthorTitle ?? graph.Author?.Title,
            confirmed?.ManagerName ?? graph.Manager?.Name,
            confirmed?.ManagerTitle ?? graph.Manager?.Title);

        confirmedIdentities[email] = identity;
        return identity;
    }

    while (true)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var calendarWeekdays = WorkingCalendar.CountWeekdays(today.Year, today.Month);
        var hrWorkingDays = HrCalendar.GetWorkingDays(today.Year, today.Month);

        var input = Prompts.AskReportInput(
            defaultEmail, today.ToString("MMMM yyyy"), calendarWeekdays, hrWorkingDays, lastInput);
        lastInput = input;

        MonthlyKupReport report;
        try
        {
            report = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Fetching pull requests...", async ctx =>
                {
                    var from = monthStart.AddDays(-input.DaysBefore);
                    var pullRequests = new List<CollectedPullRequest>();

                    ctx.Status("Resolving GitHub user...");
                    var login = await ResolveGitHubLoginAsync(input.Email);
                    if (login is null)
                        AnsiConsole.MarkupLine(
                            $"[yellow]Warning:[/] no GitHub user found for {Markup.Escape(input.Email)} - skipping GitHub PRs.");
                    else
                    {
                        ctx.Status("Fetching GitHub pull requests...");
                        pullRequests.AddRange(await githubApi.SearchPullRequestsAsync(login, from, today, cts.Token));
                    }

                    if (adoSession is not null)
                    {
                        ctx.Status("Resolving Azure DevOps identity...");
                        var adoId = await ResolveAdoIdentityAsync(input.Email);
                        if (adoId is null)
                            AnsiConsole.MarkupLine(
                                $"[yellow]Warning:[/] no Azure DevOps identity found for {Markup.Escape(input.Email)} - skipping ADO PRs.");
                        else
                        {
                            ctx.Status("Fetching Azure DevOps pull requests...");
                            pullRequests.AddRange(await adoApi.GetPullRequestsAsync(
                                adoSession.Organization, adoId, from, cts.Token));
                        }

                        ctx.Status("Linking work items...");
                        var enricher = new WorkItemEnricher(adoApi, adoSession.Organization);
                        pullRequests = await enricher.EnrichAsync(pullRequests, cts.Token);
                    }

                    return ReportBuilder.Build(
                        input.Email, from, today, input.WorkingDays, input.VacationDays, input.TargetPercent,
                        pullRequests);
                });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to fetch PRs:[/] {Markup.Escape(ex.Message)}");
            continue;
        }

        ReportRenderer.Render(report);
        AnsiConsole.WriteLine();

        var startOver = false;
        while (!startOver)
        {
            switch (Prompts.AskPostReportAction())
            {
                case PostReportAction.StartOver:
                    startOver = true;
                    break;

                case PostReportAction.ExportPdf:
                    var identity = await ResolveIdentityAsync(input.Email);
                    var pdfPath = Prompts.AskPdfPath($"KUP-{input.Email.Split('@')[0]}-{today:yyyy-MM}.pdf");
                    try
                    {
                        AnsiConsole.Status()
                            .Spinner(Spinner.Known.Dots)
                            .Start("Generating PDF...", _ => KupPdfExporter.Export(report, identity, pdfPath));
                        AnsiConsole.MarkupLine($"[green]Saved[/] {Markup.Escape(Path.GetFullPath(pdfPath))}");
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[red]PDF export failed:[/] {Markup.Escape(ex.Message)}");
                    }
                    break;

                case PostReportAction.Exit:
                    return 0;
            }
        }
    }
    } // session loop
}
catch (OperationCanceledException)
{
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
    return 1;
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
    return 1;
}
