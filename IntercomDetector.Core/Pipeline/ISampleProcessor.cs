namespace IntercomDetector.Core.Pipeline;

public interface ISampleProcessor
{
    Task ProcessSampleAsync(long timestampMs, double voltage, string timeR);
}
