namespace IntercomDetector.Core.Models;

/// <summary>A single raw voltage sample with its timestamp.</summary>
public record RawSample(long TimestampMs, double Voltage);
