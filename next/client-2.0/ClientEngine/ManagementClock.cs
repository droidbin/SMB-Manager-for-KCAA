namespace SmbManager.Local.Client;

// An authenticated HTTP Date is a time reference, not an authorization decision.
// Commit it only after the signed policy has passed all normal validation.
// Never change Windows time, learn time from IssuedAt, or persist a clock anchor.
public sealed class ManagementClock(TimeProvider? local = null) : TimeProvider
{
    private readonly TimeProvider local = local ?? TimeProvider.System;
    private readonly object gate = new();
    private DateTimeOffset? anchorUtc;
    private long anchorTimestamp;
    public sealed record Sample(DateTimeOffset HeaderUtc, long ReceivedTimestamp, TimeSpan RoundTrip);
    public bool HasServerTime { get { lock(gate) return anchorUtc.HasValue; } }
    public override long GetTimestamp() => local.GetTimestamp();
    public override long TimestampFrequency => local.TimestampFrequency;
    public override DateTimeOffset GetUtcNow()
    {
        lock(gate) return anchorUtc is { } value
            ? value + local.GetElapsedTime(anchorTimestamp, local.GetTimestamp())
            : local.GetUtcNow();
    }
    public Sample? Observe(DateTimeOffset? header, long requested, long received, TimeSpan? age = null)
    {
        var elapsed=local.GetElapsedTime(requested,received);
        if(header is null || elapsed<TimeSpan.Zero || elapsed>TimeSpan.FromSeconds(10) || age>TimeSpan.Zero) return null;
        return new(header.Value.ToUniversalTime(),received,elapsed);
    }
    public DateTimeOffset VerificationTime(Sample sample)
    {
        // HTTP Date has one-second precision. Use an upper bound so a slow
        // response/rounding cannot extend the signed expiration deadline.
        var candidate=sample.HeaderUtc+sample.RoundTrip+TimeSpan.FromSeconds(1)
            +local.GetElapsedTime(sample.ReceivedTimestamp,local.GetTimestamp());
        lock(gate) return anchorUtc.HasValue && GetUtcNow()>candidate ? GetUtcNow() : candidate;
    }
    public void Commit(Sample sample)
    {
        lock(gate){anchorUtc=VerificationTime(sample);anchorTimestamp=local.GetTimestamp();}
    }
}
