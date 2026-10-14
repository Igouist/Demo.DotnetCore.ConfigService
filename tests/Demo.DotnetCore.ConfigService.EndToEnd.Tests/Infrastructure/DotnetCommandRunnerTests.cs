using System.Diagnostics;

namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

public sealed class DotnetCommandRunnerTests
{
    [Fact]
    public void Accumulator_retains_only_the_trailing_64_KiB_after_truncation()
    {
        const int retainedCharacterCount = 64 * 1024;
        var accumulator = new BoundedTextAccumulator(retainedCharacterCount);
        string retainedTail = new('x', retainedCharacterCount);

        accumulator.Append("discarded");
        accumulator.Append(retainedTail);

        Assert.Equal($"[output truncated]{Environment.NewLine}{retainedTail}", accumulator.GetText());
    }

    [Fact]
    public void Failure_diagnostics_include_only_each_streams_retained_tail()
    {
        var standardOutput = new BoundedTextAccumulator(8);
        var standardError = new BoundedTextAccumulator(8);
        standardOutput.Append("discarded-stdout");
        standardOutput.Append("OUT-TAIL");
        standardError.Append("discarded-stderr");
        standardError.Append("ERR-TAIL");

        string diagnostics = DotnetCommandRunner.FormatOutputDiagnostics(
            standardOutput,
            standardError);

        Assert.DoesNotContain("discarded", diagnostics, StringComparison.Ordinal);
        Assert.Equal(
            $"{Environment.NewLine}stdout:{Environment.NewLine}[output truncated]{Environment.NewLine}OUT-TAIL"
            + $"{Environment.NewLine}stderr:{Environment.NewLine}[output truncated]{Environment.NewLine}ERR-TAIL",
            diagnostics);
    }

    [Fact]
    public async Task Main_completion_wait_includes_both_output_pumps()
    {
        var pendingErrorPump = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new DotnetCommandCompletion(
            Task.CompletedTask,
            Task.CompletedTask,
            pendingErrorPump.Task);

        DotnetCommandWaitOutcome outcome = await completion
            .WaitForMainTimeoutAsync(TimeSpan.FromMilliseconds(25), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(DotnetCommandWaitOutcome.TimedOut, outcome);
    }

    [Fact]
    public async Task Cleanup_wait_returns_when_its_grace_period_expires()
    {
        var pendingOutputPump = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new DotnetCommandCompletion(
            Task.CompletedTask,
            pendingOutputPump.Task,
            Task.CompletedTask);
        var stopwatch = Stopwatch.StartNew();

        DotnetCommandCleanupResult result = await completion
            .WaitForCleanupGraceAsync(TimeSpan.FromMilliseconds(25))
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(DotnetCommandCleanupOutcome.TimedOut, result.Outcome);
        Assert.Null(result.Failure);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Termination_failure_does_not_skip_completion_wait_and_surfaces_both_failures()
    {
        var completionFailure = new IOException("output drain failed");
        var completionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminationFailure = new InvalidOperationException("termination failed");

        TimeoutException exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            DotnetCommandRunner.RunAsync(
                DotnetHostLocator.Resolve(),
                CreateVersionCommandOptions(),
                CancellationToken.None,
                _ =>
                {
                    completionGate.SetException(completionFailure);
                    throw terminationFailure;
                },
                (processExit, standardOutputPump, standardErrorPump) =>
                    new DotnetCommandCompletion(
                        processExit,
                        standardOutputPump,
                        Task.WhenAll(standardErrorPump, completionGate.Task))));

        IReadOnlyList<Exception> cleanupFailures = GetCleanupFailures(exception);
        Assert.Contains(terminationFailure, cleanupFailures);
        Assert.Contains(completionFailure, cleanupFailures);
    }

    [Fact]
    public async Task Faulted_cleanup_completion_is_surfaced_with_the_primary_timeout()
    {
        var completionFailure = new TimeoutException("output drain failed");
        var completionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        TimeoutException exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            DotnetCommandRunner.RunAsync(
                DotnetHostLocator.Resolve(),
                CreateVersionCommandOptions(),
                CancellationToken.None,
                _ => completionGate.SetException(completionFailure),
                (processExit, standardOutputPump, standardErrorPump) =>
                    new DotnetCommandCompletion(
                        processExit,
                        standardOutputPump,
                        Task.WhenAll(standardErrorPump, completionGate.Task))));

        Assert.Contains(completionFailure, GetCleanupFailures(exception));
    }

    [Fact]
    public async Task Caller_cancellation_preserves_its_token_when_cleanup_fails()
    {
        var completionFailure = new IOException("output drain failed");
        var completionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        OperationCanceledException exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            DotnetCommandRunner.RunAsync(
                DotnetHostLocator.Resolve(),
                CreateVersionCommandOptions(),
                cancellationSource.Token,
                _ => completionGate.SetException(completionFailure),
                (processExit, standardOutputPump, standardErrorPump) =>
                    new DotnetCommandCompletion(
                        processExit,
                        standardOutputPump,
                        Task.WhenAll(standardErrorPump, completionGate.Task))));

        Assert.Equal(cancellationSource.Token, exception.CancellationToken);
        Assert.Contains(completionFailure, GetCleanupFailures(exception));
    }

    [Fact]
    public async Task Caller_cancellation_wins_over_the_main_timeout()
    {
        var pendingExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new DotnetCommandCompletion(
            pendingExit.Task,
            Task.CompletedTask,
            Task.CompletedTask);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        DotnetCommandWaitOutcome outcome = await completion.WaitForMainTimeoutAsync(
            TimeSpan.Zero,
            cancellationSource.Token);

        Assert.Equal(DotnetCommandWaitOutcome.Canceled, outcome);
    }

    private static DotnetCommandOptions CreateVersionCommandOptions()
    {
        return new DotnetCommandOptions(
            AppContext.BaseDirectory,
            ["--version"],
            new Dictionary<string, string>(),
            TimeSpan.Zero);
    }

    private static IReadOnlyList<Exception> GetCleanupFailures(Exception exception)
    {
        Exception cleanupFailure = Assert.IsAssignableFrom<Exception>(exception.InnerException);
        return cleanupFailure is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions
            : [cleanupFailure];
    }
}
