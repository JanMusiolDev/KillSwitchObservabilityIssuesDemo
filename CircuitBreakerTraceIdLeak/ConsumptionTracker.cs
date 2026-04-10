namespace CircuitBreakerTraceIdLeak;

public sealed class ConsumptionTracker
{
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _lastExpectedIndex = -1;
    private int _lastProcessedIndex = -1;

    public void ExpectLastIndex(int index) => _lastExpectedIndex = index;

    public void MessageProcessed(int index)
    {
        _lastProcessedIndex = index;
        if (_lastProcessedIndex >= _lastExpectedIndex)
            _done.TrySetResult();

    }

    public Task WaitAsync(CancellationToken ct)
    {
        ct.Register(() => _done.TrySetCanceled(ct));
        return _done.Task;
    }
}
