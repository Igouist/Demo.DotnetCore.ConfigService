namespace Demo.DotnetCore.ConfigService.Client.Tests.Infrastructure;

internal sealed class FakeDistributionTransport : IDistributionTransport
{
    private readonly Queue<Func<CancellationToken, Task<DistributionHttpResponse>>> _steps = [];
    private readonly SemaphoreSlim _calls = new(0);

    internal int CallCount { get; private set; }

    internal List<DistributionResolveRequest> Requests { get; } = [];

    internal void EnqueueResponse(DistributionHttpResponse response) =>
        _steps.Enqueue(_ => Task.FromResult(response));

    internal void EnqueueFailure(Exception exception) =>
        _steps.Enqueue(_ => Task.FromException<DistributionHttpResponse>(exception));

    internal void EnqueueBlockingResponse() =>
        _steps.Enqueue(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocking response cannot complete normally.");
        });

    internal Task EnqueueSynchronouslyBlockingResponse(
        BlockingCallbackGate startGate,
        DistributionHttpResponse response)
    {
        var finished = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _steps.Enqueue(_ =>
        {
            try
            {
                startGate.Block();
                return Task.FromResult(response);
            }
            finally
            {
                finished.TrySetResult(true);
            }
        });
        return finished.Task;
    }

    internal TaskCompletionSource<DistributionHttpResponse> EnqueueNonCooperativeResponse()
    {
        var completion = new TaskCompletionSource<DistributionHttpResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _steps.Enqueue(_ => completion.Task);
        return completion;
    }

    internal TaskCompletionSource<DistributionHttpResponse> EnqueueNonCooperativeResponse(
        Action cancellationCallback)
    {
        var completion = new TaskCompletionSource<DistributionHttpResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _steps.Enqueue(cancellationToken =>
        {
            _ = cancellationToken.UnsafeRegister(
                static state => ((Action)state!).Invoke(),
                cancellationCallback);
            return completion.Task;
        });
        return completion;
    }

    internal Task WaitForNextCallAsync() => _calls.WaitAsync();

    public Task<DistributionHttpResponse> ResolveAsync(
        Uri endpoint,
        DistributionResolveRequest request,
        CancellationToken cancellationToken)
    {
        CallCount++;
        Requests.Add(request);
        _calls.Release();
        return _steps.Dequeue()(cancellationToken);
    }
}
