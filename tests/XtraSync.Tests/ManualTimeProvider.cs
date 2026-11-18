namespace XtraSync.Tests;

/// <summary>Controls report throttling without sleeping or relying on machine speed.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private long timestamp;
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => timestamp;
    public void Advance(int milliseconds) => timestamp += milliseconds;
}
