namespace KupReport.Reporting;

/// <summary>
/// Spreads a budget of KUP hours across pull requests proportionally to their size.
/// </summary>
public static class KupAllocator
{
    /// <summary>Granularity of assigned hours; also the minimum a tagged PR receives.</summary>
    public const decimal Step = 0.5m;

    private const double DeletionFactor = 0.5;
    private const double FileFactor = 3.0;

    /// <summary>
    /// Size of a pull request: <c>√(additions + ½·deletions) + 3·√(changed files)</c>.
    /// Square roots give diminishing returns, so one huge (often generated) PR
    /// does not swallow the whole budget; deletions count half as much as additions.
    /// </summary>
    public static double Weight(int additions, int deletions, int changedFiles) =>
        Math.Sqrt(Math.Max(0, additions) + DeletionFactor * Math.Max(0, deletions)) +
        FileFactor * Math.Sqrt(Math.Max(0, changedFiles));

    /// <summary>
    /// Splits <paramref name="budget"/> (rounded down to <see cref="Step"/>) across the
    /// given weights. Every PR receives at least <see cref="Step"/> hours; shares that
    /// would fall below it are pinned to the minimum and the rest is re-split among the
    /// others. Shares are rounded to <see cref="Step"/> by largest remainder, so the
    /// total equals the rounded budget. When the budget cannot give every PR the
    /// minimum, the heaviest PRs get <see cref="Step"/> each and the rest get 0.
    /// </summary>
    public static decimal[] Allocate(decimal budget, IReadOnlyList<double> weights)
    {
        var count = weights.Count;
        var result = new decimal[count];
        var units = budget > 0 ? (int)Math.Floor(budget / Step) : 0;
        if (count == 0 || units == 0)
            return result;

        var unitShares = new int[count];

        if (units < count)
        {
            foreach (var i in ByWeightDescending(weights, Enumerable.Range(0, count)).Take(units))
                unitShares[i] = 1;
        }
        else
        {
            var pinned = new bool[count];
            var free = Enumerable.Range(0, count).ToList();
            decimal[] shares;

            // Pin PRs whose proportional share is below the minimum until all free
            // shares reach it. Terminates: the free budget always averages >= 1 unit.
            while (true)
            {
                var freeUnits = units - (count - free.Count);
                shares = ProportionalShares(freeUnits, weights, free, count);

                var belowMinimum = free.Where(i => shares[i] < 1m).ToList();
                if (belowMinimum.Count == 0)
                    break;

                foreach (var i in belowMinimum)
                    pinned[i] = true;
                free.RemoveAll(i => pinned[i]);
            }

            for (var i = 0; i < count; i++)
                unitShares[i] = pinned[i] ? 1 : (int)Math.Floor(shares[i]);

            var leftover = units - unitShares.Sum();
            foreach (var i in free
                         .OrderByDescending(i => shares[i] - Math.Floor(shares[i]))
                         .ThenByDescending(i => weights[i])
                         .ThenBy(i => i)
                         .Take(Math.Max(0, leftover)))
                unitShares[i]++;
        }

        for (var i = 0; i < count; i++)
            result[i] = unitShares[i] * Step;
        return result;
    }

    private static decimal[] ProportionalShares(int units, IReadOnlyList<double> weights, List<int> indices, int count)
    {
        var shares = new decimal[count];
        var totalWeight = indices.Sum(i => SafeWeight(weights[i]));

        foreach (var i in indices)
        {
            shares[i] = totalWeight > 0
                ? units * (decimal)SafeWeight(weights[i]) / (decimal)totalWeight
                : (decimal)units / indices.Count;
        }

        return shares;
    }

    private static double SafeWeight(double weight) => double.IsFinite(weight) && weight > 0 ? weight : 0;

    private static IEnumerable<int> ByWeightDescending(IReadOnlyList<double> weights, IEnumerable<int> indices) =>
        indices.OrderByDescending(i => SafeWeight(weights[i])).ThenBy(i => i);
}
