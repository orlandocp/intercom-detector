using IntercomDetector.Core.Pipeline;

namespace IntercomDetector.Core;

/// <summary>
/// Orchestrates a list of ISampleProcessor instances.
/// Each valid sample is forwarded to every registered processor in order.
/// </summary>
public class SamplePipeline
{
    private readonly List<ISampleProcessor> _processors;

    public SamplePipeline(IEnumerable<ISampleProcessor> processors)
    {
        _processors = processors.ToList();
    }

    public async Task ProcessAsync(long timestampMs, double voltage, string timeR)
    {
        foreach (var processor in _processors)
            await processor.ProcessSampleAsync(timestampMs, voltage, timeR);
    }
}
