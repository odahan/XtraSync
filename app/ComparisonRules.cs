namespace XtraSync;

public sealed record ComparisonSettings(
    bool CompareSize = true,
    bool CompareTimestamps = true,
    int ToleranceSeconds = 2,
    bool IgnoreTimeZone = false,
    bool IncludeSubfolders = true,
    string Exclusions = "",
    string FileExclusions = "",
    string FolderExclusions = "",
    bool DeleteExcludedFromDestination = false,
    RunLogStore? RunArtifacts = null, ErrorPolicy? ErrorPolicy = null);

public enum MirrorMode
{
    Left = 3,
    Right = 4,
    Both = 5
}

/// <summary>Implements the verified FILETIME rules from original function 0x00996A00.</summary>
public static class ComparisonRules
{
    public static int CompareFileTimes(long left, long right, int toleranceSeconds,
        byte flags = 0, int daylightShiftSeconds = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(toleranceSeconds);
        if (left == 0 || right == 0)
        {
            return 0;
        }

        long difference = Math.Abs(left / 10_000_000 - right / 10_000_000);
        int order = difference == 0 ? 0 : left < right ? -1 : 1;
        if (difference <= toleranceSeconds ||
            ((flags & 1) != 0 && Math.Abs(difference - daylightShiftSeconds) <= toleranceSeconds))
        {
            return 0;
        }

        if ((flags & 2) != 0 && difference / 3600 < 24)
        {
            long remainder = difference % 3600;
            if (remainder > 1800)
            {
                remainder = 3600 - remainder;
            }

            if (remainder <= toleranceSeconds)
            {
                return 0;
            }
        }

        if (order == 0 || (flags & 12) == 0 || difference > 86399)
        {
            return order;
        }

        DateTime first = DateTime.FromFileTimeUtc(left);
        DateTime second = DateTime.FromFileTimeUtc(right);
        if (first.Date != second.Date)
        {
            return order;
        }

        bool firstIncomplete = (flags & 4) != 0;
        bool secondIncomplete = (flags & 8) != 0;
        if ((firstIncomplete && first.Hour == 0 && first.Minute == 0 && first.Second == 0) ||
            (secondIncomplete && second.Hour == 0 && second.Minute == 0 && second.Second == 0))
        {
            return 0;
        }

        if (first.Hour != second.Hour || first.Minute != second.Minute)
        {
            return order;
        }

        return (firstIncomplete && first.Second == 0) || (secondIncomplete && second.Second == 0)
            ? 0 : order;
    }
}
