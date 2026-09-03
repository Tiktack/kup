namespace KupReport.Reporting;

public static class WorkingCalendar
{
    /// <summary>Counts Monday-Friday days in the given month.</summary>
    public static int CountWeekdays(int year, int month)
    {
        var days = DateTime.DaysInMonth(year, month);
        var count = 0;
        for (var day = 1; day <= days; day++)
        {
            var dow = new DateOnly(year, month, day).DayOfWeek;
            if (dow is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
                count++;
        }
        return count;
    }
}
