using System.Diagnostics;
using KupReport.AzureDevOps;
using Spectre.Console;

namespace KupReport.Authentication;

public sealed record AdoSession(string Organization, string DisplayName, string UserId);

/// <summary>Handles Entra ID device-code authentication for Azure DevOps.</summary>
public sealed class AdoAuthenticator(AdoApiClient api)
{
    private static string ClientId => AdoApiClient.ClientId;

    /// <summary>Tries to authenticate silently with the cached refresh token.</summary>
    public async Task<AdoSession?> TryCachedLoginAsync(CancellationToken ct)
    {
        var credentials = CredentialStore.Load();
        if (credentials.AdoRefreshToken is null || credentials.AdoOrganization is null)
            return null;

        var token = await api.RefreshTokenAsync(ClientId, credentials.AdoRefreshToken, ct);
        if (token?.AccessToken is not { Length: > 0 } accessToken)
            return null;

        if (token.RefreshToken is { Length: > 0 } rotated)
            CredentialStore.Update(c => c.AdoRefreshToken = rotated);

        api.SetToken(accessToken);
        var identity = await api.GetAuthenticatedUserAsync(credentials.AdoOrganization, ct);
        return identity is null
            ? null
            : new AdoSession(credentials.AdoOrganization, identity.DisplayName, identity.Id);
    }

    /// <summary>Runs the interactive device flow and verifies access to the given organization.</summary>
    public async Task<AdoSession> LoginAsync(string organization, CancellationToken ct)
    {
        var device = await api.RequestDeviceCodeAsync(ClientId, ct);

        AnsiConsole.Write(new Panel(
                new Rows(
                    new Markup($"Open   [link blue]{Markup.Escape(device.VerificationUri)}[/]"),
                    new Markup($"Enter  [bold yellow]{Markup.Escape(device.UserCode)}[/]")))
            .Header(" Azure DevOps Login ")
            .BorderColor(Color.Blue)
            .Padding(2, 1));

        TryOpenBrowser(device.VerificationUri);

        var token = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Waiting for authorization in the browser...",
                _ => PollForTokenAsync(device, ct));

        api.SetToken(token.AccessToken!);

        var identity = await api.GetAuthenticatedUserAsync(organization, ct)
            ?? throw new InvalidOperationException(
                $"Authenticated, but access to organization '{organization}' failed. Check the organization name.");

        CredentialStore.Update(c =>
        {
            c.AdoRefreshToken = token.RefreshToken;
            c.AdoOrganization = organization;
        });

        return new AdoSession(organization, identity.DisplayName, identity.Id);
    }

    private async Task<AdoTokenResponse> PollForTokenAsync(AdoDeviceCodeResponse device, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(device.Interval, 5));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(device.ExpiresIn);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval, ct);

            var token = await api.ExchangeDeviceCodeAsync(ClientId, device.DeviceCode, ct);
            if (token?.AccessToken is { Length: > 0 })
                return token;

            switch (token?.Error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                default:
                    throw new InvalidOperationException(
                        $"Authorization failed: {token?.Error} {FirstLine(token?.ErrorDescription)}".Trim());
            }
        }

        throw new TimeoutException("Device authorization timed out. Please try again.");
    }

    private static string? FirstLine(string? text) =>
        text?.Split('\n', '\r').FirstOrDefault(line => line.Length > 0);

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
