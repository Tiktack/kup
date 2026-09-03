namespace KupReport.Reporting;

/// <summary>Working days per month as published by HR.</summary>
public static class HrCalendar
{
    private static readonly Dictionary<(int Year, int Month), int> WorkingDays = new()
    {
        [(2026, 1)] = 20,
        [(2026, 2)] = 20,
        [(2026, 3)] = 22,
        [(2026, 4)] = 21,
        [(2026, 5)] = 20,
        [(2026, 6)] = 21,
        [(2026, 7)] = 23,
        [(2026, 8)] = 20,
        [(2026, 9)] = 22,
        [(2026, 10)] = 22,
        [(2026, 11)] = 20,
        [(2026, 12)] = 20,
    };

    public static int? GetWorkingDays(int year, int month) =>
        WorkingDays.TryGetValue((year, month), out var days) ? days : null;
}
