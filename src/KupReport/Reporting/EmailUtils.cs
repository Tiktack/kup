namespace KupReport.Reporting;

public static class EmailUtils
{
    /// <summary>
    /// Normalizes an email for comparison and display: lowercases and strips
    /// a "+alias" suffix from the local part (common on enterprise accounts,
    /// e.g. user+org@company.com -> user@company.com).
    /// </summary>
    public static string Normalize(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0)
            return email.Trim().ToLowerInvariant();

        var local = email[..at];
        var plus = local.IndexOf('+');
        if (plus > 0)
            local = local[..plus];

        return $"{local}{email[at..]}".Trim().ToLowerInvariant();
    }

    public static bool Same(string? left, string? right) =>
        left is not null && right is not null && Normalize(left) == Normalize(right);
}
