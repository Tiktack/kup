using System.Diagnostics;
using KupReport.GitHub;
using Spectre.Console;

namespace KupReport.Authentication;

public sealed record AuthenticatedUser(string Login, string Token);

/// <summary>Handles the GitHub OAuth device flow and cached-token reuse.</summary>
public sealed class GitHubAuthenticator(GitHubApiClient api)
{
    // GitHub CLI's public OAuth client id; override with KUP_GITHUB_CLIENT_ID.
    private const string DefaultClientId = "178c6fc778ccc68e1d6a";

    private static string ClientId =>
        Environment.GetEnvironmentVariable("KUP_GITHUB_CLIENT_ID") ?? DefaultClientId;

    /// <summary>Tries to authenticate with a previously cached token.</summary>
    public async Task<AuthenticatedUser?> TryCachedLoginAsync(CancellationToken ct)
    {
        var token = CredentialStore.Load().GitHubToken;
        if (token is null)
            return null;

        var login = await api.GetLoginAsync(token, ct);
        if (login is null)
            return null;

        api.SetToken(token);
        return new AuthenticatedUser(login, token);
    }

    /// <summary>Runs the interactive device flow: shows the code, opens the browser, polls for the token.</summary>
    public async Task<AuthenticatedUser> LoginAsync(CancellationToken ct)
    {
        var device = await api.RequestDeviceCodeAsync(ClientId, ct);

        AnsiConsole.Write(new Panel(
                new Rows(
                    new Markup($"Open   [link blue]{Markup.Escape(device.VerificationUri)}[/]"),
                    new Markup($"Enter  [bold yellow]{Markup.Escape(device.UserCode)}[/]")))
            .Header(" GitHub Login ")
            .BorderColor(Color.Blue)
            .Padding(2, 1));

        TryOpenBrowser(device.VerificationUri);

        var token = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Waiting for authorization in the browser...",
                _ => PollForTokenAsync(device, ct));

        CredentialStore.Update(c => c.GitHubToken = token);
        api.SetToken(token);

        var login = await api.GetLoginAsync(token, ct)
            ?? throw new InvalidOperationException("Authenticated but failed to read user profile.");
        return new AuthenticatedUser(login, token);
    }

    private async Task<string> PollForTokenAsync(DeviceCodeResponse device, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(device.Interval, 5));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(device.ExpiresIn);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval, ct);

            var token = await api.ExchangeDeviceCodeAsync(ClientId, device.DeviceCode, ct);
            if (token?.AccessToken is { Length: > 0 } accessToken)
                return accessToken;

            switch (token?.Error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval = TimeSpan.FromSeconds(token.Interval ?? interval.TotalSeconds + 5);
                    continue;
                default:
                    throw new InvalidOperationException(
                        $"Authorization failed: {token?.Error} {token?.ErrorDescription}".Trim());
            }
        }

        throw new TimeoutException("Device authorization timed out. Please run the tool again.");
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            AnsiConsole.MarkupLine("[grey]Could not open the browser automatically - open the URL above manually.[/]");
        }
    }
}
