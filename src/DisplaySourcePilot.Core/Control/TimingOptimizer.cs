namespace DisplaySourcePilot.Core.Control;

public static class TimingOptimizer
{
    public static int CalculateMenuDelay(
        SamsungSourceProfile profile,
        IEnumerable<TimeSpan> observedRoundTrips) =>
        CalculateMenuDelay(
            profile.RenderBudgetMilliseconds,
            observedRoundTrips,
            profile.MinimumDelayMilliseconds,
            profile.MaximumDelayMilliseconds);

    public static int CalculateMenuDelay(
        int renderBudgetMilliseconds,
        IEnumerable<TimeSpan> observedRoundTrips,
        int minimumDelayMilliseconds,
        int maximumDelayMilliseconds)
    {
        if (renderBudgetMilliseconds < 0 ||
            minimumDelayMilliseconds < 0 ||
            maximumDelayMilliseconds < minimumDelayMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(renderBudgetMilliseconds));
        }

        var observations = observedRoundTrips
            .Select(value => value.TotalMilliseconds)
            .Where(value => value >= 0 && double.IsFinite(value))
            .Order()
            .ToArray();

        var networkBudget = observations.Length == 0
            ? 50
            : Percentile(observations, 0.95) * 2;

        var rawDelay = renderBudgetMilliseconds + networkBudget;
        var rounded = (int)(Math.Ceiling(rawDelay / 10) * 10);
        return Math.Clamp(rounded, minimumDelayMilliseconds, maximumDelayMilliseconds);
    }

    private static double Percentile(double[] sortedValues, double percentile)
    {
        if (sortedValues.Length == 1)
        {
            return sortedValues[0];
        }

        var position = percentile * (sortedValues.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        var fraction = position - lower;
        return sortedValues[lower] + ((sortedValues[upper] - sortedValues[lower]) * fraction);
    }
}
