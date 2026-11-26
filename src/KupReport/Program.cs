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

    // Proposes [KUP:x] tags for merged GitHub PRs and writes them after confirmation.
    // Returns the PR list refreshed with the latest GitHub titles/bodies (including
    // written tags), or null when cancelled before anything was read.
    async Task<List<CollectedPullRequest>?> AssignKupTagsAsync(
        MonthlyKupReport report, List<CollectedPullRequest> pullRequests)
    {
        if (Prompts.AskAutoTagMode() is not { } mode)
            return null;

        var eligible = pullRequests.Where(pr => KupAutoTagger.IsCandidate(pr, mode)).ToList();
        if (eligible.Count == 0)
        {
            AnsiConsole.MarkupLine(mode == AutoTagMode.FillRemaining
                ? "[yellow]No merged GitHub PRs without a [[KUP:x]] tag.[/]"
                : "[yellow]No merged GitHub PRs to tag.[/]");
            return null;
        }

        var results = new AutoTagCandidate?[eligible.Count];
        var readErrors = new string?[eligible.Count];
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Reading PR sizes...", async ctx =>
            {
                var done = 0;
                await Parallel.ForEachAsync(
                    Enumerable.Range(0, eligible.Count),
                    new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cts.Token },
                    async (i, ct) =>
                    {
                        var pr = eligible[i];
                        try
                        {
                            var details = await githubApi.GetPullRequestAsync(pr.Repository, pr.Number, ct);
                            results[i] = new AutoTagCandidate(
                                pr, details.Title, details.Body,
                                new PullRequestChangeStats(details.Additions, details.Deletions, details.ChangedFiles));
                        }
                        catch (Exception ex) when (!ct.IsCancellationRequested)
                        {
                            readErrors[i] = ex.Message;
                        }
                        ctx.Status($"Reading PR sizes ({Interlocked.Increment(ref done)}/{eligible.Count})...");
                    });
            });

        for (var i = 0; i < eligible.Count; i++)
        {
            if (readErrors[i] is { } error)
                AnsiConsole.MarkupLine(
                    $"[yellow]Skipped[/] {Markup.Escape($"{eligible[i].Repository}#{eligible[i].Number}")}: {Markup.Escape(error)}");
        }

        var candidates = results.OfType<AutoTagCandidate>().ToList();

        // Latest title/body per PR, so the rebuilt report matches what the plan saw.
        var latest = candidates.ToDictionary(c => c.PullRequest, c => (c.Title, c.Body));
        List<CollectedPullRequest> WithLatestText() => pullRequests
            .Select(pr => latest.TryGetValue(pr, out var text) ? pr with { Title = text.Title, Body = text.Body } : pr)
            .ToList();

        var plan = KupAutoTagger.Plan(mode, report.TargetHours, pullRequests, candidates);
        ReportRenderer.RenderAutoTagPlan(plan, report);
        AnsiConsole.WriteLine();

        var changes = plan.Changes;
        if (changes.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]Nothing to update.[/]");
            return WithLatestText();
        }

        if (!Prompts.ConfirmAutoTag(changes.Count))
            return WithLatestText();

        var updatedCount = 0;
        var writeErrors = new List<string>();
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Updating pull requests...", async ctx =>
            {
                foreach (var change in changes)
                {
                    var pr = change.Candidate.PullRequest;
                    ctx.Status($"Updating pull requests ({updatedCount + writeErrors.Count + 1}/{changes.Count})...");
                    try
                    {
                        await githubApi.UpdatePullRequestAsync(
                            pr.Repository, pr.Number,
                            change.TitleChanged ? change.NewTitle : null,
                            change.BodyChanged ? change.NewBody ?? "" : null,
                            cts.Token);
                        latest[pr] = (change.NewTitle, change.NewBody);
                        updatedCount++;
                    }
                    catch (Exception ex) when (!cts.IsCancellationRequested)
                    {
                        writeErrors.Add($"{pr.Repository}#{pr.Number}: {ex.Message}");
                    }
                }
            });

        foreach (var error in writeErrors)
            AnsiConsole.MarkupLine($"[red]Failed[/] {Markup.Escape(error)}");
        AnsiConsole.MarkupLine(updatedCount > 0
            ? $"[green]Updated {updatedCount} pull request(s).[/]"
            : "[yellow]No pull requests were updated.[/]");

        return WithLatestText();
    }

    while (true)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var input = Prompts.AskReportInput(defaultEmail, today, lastInput);
        lastInput = input;
        var from = input.MonthStart.AddDays(-input.DaysBefore);
        var to = input.MonthStart == new DateOnly(today.Year, today.Month, 1)
            ? today
            : input.MonthStart.AddMonths(1).AddDays(-1);

        MonthlyKupReport BuildReport(IEnumerable<CollectedPullRequest> prs) => ReportBuilder.Build(
            input.Email, from, to, input.WorkingDays, input.VacationDays, input.TargetPercent, prs);

        List<CollectedPullRequest> collected;
        try
        {
            collected = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Fetching pull requests...", async ctx =>
                {
                    var pullRequests = new List<CollectedPullRequest>();

                    ctx.Status("Resolving GitHub user...");
                    var login = await ResolveGitHubLoginAsync(input.Email);
                    if (login is null)
                        AnsiConsole.MarkupLine(
                            $"[yellow]Warning:[/] no GitHub user found for {Markup.Escape(input.Email)} - skipping GitHub PRs.");
                    else
                    {
                        ctx.Status("Fetching GitHub pull requests...");
                        pullRequests.AddRange(await githubApi.SearchPullRequestsAsync(login, from, to, cts.Token));
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
                                adoSession.Organization, adoId, from, to, cts.Token));
                        }

                        ctx.Status("Linking work items...");
                        var enricher = new WorkItemEnricher(adoApi, adoSession.Organization);
                        pullRequests = await enricher.EnrichAsync(pullRequests, cts.Token);
                    }

                    return pullRequests;
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

        var report = BuildReport(collected);
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
                    var pdfPath = Prompts.AskPdfPath($"KUP-{input.Email.Split('@')[0]}-{input.MonthStart:yyyy-MM}.pdf");
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

                case PostReportAction.AssignTags:
                    var retagged = await AssignKupTagsAsync(report, collected);
                    if (retagged is not null && !retagged.SequenceEqual(collected))
                    {
                        collected = retagged;
                        report = BuildReport(collected);
                        ReportRenderer.Render(report);
                    }
                    AnsiConsole.WriteLine();
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
