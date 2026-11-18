namespace XtraSync.Tests;

public sealed class ComparisonRulesTests
{
    private static long Time(int seconds = 0) => new DateTime(2026, 1, 15, 12, 30, 15, DateTimeKind.Utc)
        .AddSeconds(seconds).ToFileTimeUtc();

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void UnknownTimestampComparesEqual(long left, long right) =>
        Assert.Equal(0, ComparisonRules.CompareFileTimes(left, right, 0));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 0)]
    [InlineData(-2, 0)]
    [InlineData(3, -1)]
    [InlineData(-3, 1)]
    public void ToleranceIsInclusiveAndOrderIsPreserved(int seconds, int expected) =>
        Assert.Equal(expected, ComparisonRules.CompareFileTimes(Time(), Time(seconds), 2));

    [Fact]
    public void SecondsAreTruncatedBeforeSubtracting()
    {
        Assert.Equal(0, ComparisonRules.CompareFileTimes(Time(), Time() + 9_999_999, 0));
        Assert.Equal(-1, ComparisonRules.CompareFileTimes(Time() + 9_999_999, Time(1), 0));
    }

    [Fact]
    public void NegativeToleranceIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ComparisonRules.CompareFileTimes(0, 0, -1));

    [Theory]
    [InlineData(3598, 1, 3600, 0)]
    [InlineData(3602, 1, 3600, 0)]
    [InlineData(3603, 1, 3600, -1)]
    [InlineData(3600, 0, 3600, -1)]
    [InlineData(1800, 1, 1800, 0)]
    [InlineData(3600, 1, 0, -1)]
    public void DaylightSavingUsesOnlyExplicitShift(int seconds, byte flags, int shift, int expected) =>
        Assert.Equal(expected, ComparisonRules.CompareFileTimes(Time(), Time(seconds), 2, flags, shift));

    [Theory]
    [InlineData(3600, 0)]
    [InlineData(3598, 0)]
    [InlineData(3602, 0)]
    [InlineData(3603, -1)]
    [InlineData(1800, -1)]
    [InlineData(82802, 0)]
    [InlineData(86399, 0)]
    [InlineData(86400, -1)]
    [InlineData(-3600, 0)]
    public void WholeHourRuleHonorsRoundingAndDayCutoff(int seconds, int expected) =>
        Assert.Equal(expected, ComparisonRules.CompareFileTimes(Time(), Time(seconds), 2, 2));

    [Theory]
    [InlineData("2026-01-15T00:00:00Z", "2026-01-15T23:59:59Z", 4, 0)]
    [InlineData("2026-01-15T23:59:59Z", "2026-01-15T00:00:00Z", 8, 0)]
    [InlineData("2026-01-15T12:30:00Z", "2026-01-15T12:30:59Z", 4, 0)]
    [InlineData("2026-01-15T12:30:59Z", "2026-01-15T12:30:00Z", 8, 0)]
    [InlineData("2026-01-15T12:30:01Z", "2026-01-15T12:30:59Z", 4, -1)]
    [InlineData("2026-01-15T12:30:00Z", "2026-01-15T12:31:00Z", 4, -1)]
    [InlineData("2026-01-15T12:30:00Z", "2026-01-15T13:30:00Z", 4, -1)]
    [InlineData("2026-01-15T23:59:00Z", "2026-01-16T00:00:00Z", 8, -1)]
    [InlineData("2026-01-15T00:00:00Z", "2026-01-16T00:00:00Z", 4, -1)]
    [InlineData("2026-01-15T00:00:00Z", "2026-01-15T12:00:00Z", 8, -1)]
    public void IncompleteDatesMatchOnlyFlaggedComponents(string left, string right, byte flags, int expected)
    {
        Assert.Equal(expected, ComparisonRules.CompareFileTimes(
            DateTime.Parse(left).ToUniversalTime().ToFileTimeUtc(),
            DateTime.Parse(right).ToUniversalTime().ToFileTimeUtc(), 0, flags));
    }
}
