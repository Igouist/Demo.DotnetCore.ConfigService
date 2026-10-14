using System.Buffers;
using System.Diagnostics;
using System.Text;

namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

internal sealed record DotnetCommandOptions(
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    TimeSpan Timeout);

internal sealed record DotnetCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Elapsed);

internal sealed class BoundedTextAccumulator
{
    private const string TruncationMarker = "[output truncated]";

    private readonly int maximumCharacterCount;
    private readonly StringBuilder retainedText;
    private readonly object sync = new();
    private bool truncated;

    internal BoundedTextAccumulator(int maximumCharacterCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacterCount);
        this.maximumCharacterCount = maximumCharacterCount;
        retainedText = new StringBuilder(maximumCharacterCount);
    }

    internal void Append(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return;
        }

        lock (sync)
        {
            if (value.Length >= maximumCharacterCount)
            {
                truncated = retainedText.Length > 0 || value.Length > maximumCharacterCount;
                retainedText.Clear();
                retainedText.Append(value[^maximumCharacterCount..]);
                return;
            }

            retainedText.Append(value);
            int excessCharacterCount = retainedText.Length - maximumCharacterCount;
            if (excessCharacterCount > 0)
            {
                retainedText.Remove(0, excessCharacterCount);
                truncated = true;
            }
        }
    }

    internal string GetText()
    {
        lock (sync)
        {
            string text = retainedText.ToString();
            return truncated
                ? $"{TruncationMarker}{Environment.NewLine}{text}"
                : text;
        }
    }
}

internal enum DotnetCommandWaitOutcome
{
    Completed,
    TimedOut,
    Canceled
}

internal enum DotnetCommandCleanupOutcome
{
    Completed,
    TimedOut,
    Faulted
}

internal sealed record DotnetCommandCleanupResult(
    DotnetCommandCleanupOutcome Outcome,
    Exception? Failure);

internal sealed class DotnetCommandCompletion
{
    private readonly Task completionTask;

    internal DotnetCommandCompletion(
        Task processExitTask,
        Task standardOutputPump,
        Task standardErrorPump)
    {
        completionTask = Task.WhenAll(processExitTask, standardOutputPump, standardErrorPump);
    }

    internal async Task<DotnetCommandWaitOutcome> WaitForMainTimeoutAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            await completionTask.WaitAsync(linkedSource.Token);
            return DotnetCommandWaitOutcome.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DotnetCommandWaitOutcome.Canceled;
        }
        catch (OperationCanceledException)
        {
            return DotnetCommandWaitOutcome.TimedOut;
        }
    }

    internal async Task<DotnetCommandCleanupResult> WaitForCleanupGraceAsync(TimeSpan gracePeriod)
    {
        using var graceSource = new CancellationTokenSource(gracePeriod);
        try
        {
            await completionTask.WaitAsync(graceSource.Token);
            return new DotnetCommandCleanupResult(
                DotnetCommandCleanupOutcome.Completed,
                null);
        }
        catch (OperationCanceledException) when (graceSource.IsCancellationRequested)
        {
            return new DotnetCommandCleanupResult(
                DotnetCommandCleanupOutcome.TimedOut,
                null);
        }
        catch (Exception exception)
        {
            return new DotnetCommandCleanupResult(
                DotnetCommandCleanupOutcome.Faulted,
                completionTask.Exception ?? exception);
        }
    }

    internal void ObserveIncompleteFault()
    {
        _ = completionTask.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

internal static class DotnetCommandRunner
{
    private const int MaximumRetainedOutputCharacters = 64 * 1024;
    private const int OutputReadBufferCharacters = 4 * 1024;
    private static readonly TimeSpan CleanupGracePeriod = TimeSpan.FromSeconds(10);

    internal static Task<DotnetCommandResult> RunAsync(
        string dotnetHostPath,
        DotnetCommandOptions options,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(
            dotnetHostPath,
            options,
            cancellationToken,
            TryKillProcessTree,
            static (processExit, standardOutputPump, standardErrorPump) =>
                new DotnetCommandCompletion(
                    processExit,
                    standardOutputPump,
                    standardErrorPump));
    }

    internal static async Task<DotnetCommandResult> RunAsync(
        string dotnetHostPath,
        DotnetCommandOptions options,
        CancellationToken cancellationToken,
        Action<Process> terminateProcessTree,
        Func<Task, Task, Task, DotnetCommandCompletion> createCompletion)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = dotnetHostPath,
            WorkingDirectory = options.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment.Clear();
        foreach (string argument in options.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach ((string name, string value) in options.Environment)
        {
            startInfo.Environment[name] = value;
        }

        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();
        if (!process.Start())
        {
            throw new InvalidOperationException("The dotnet process could not be started.");
        }

        var standardOutput = new BoundedTextAccumulator(MaximumRetainedOutputCharacters);
        var standardError = new BoundedTextAccumulator(MaximumRetainedOutputCharacters);
        using var stopOutputPumps = new CancellationTokenSource();
        Task standardOutputPump = PumpOutputAsync(
            process.StandardOutput,
            standardOutput,
            stopOutputPumps.Token);
        Task standardErrorPump = PumpOutputAsync(
            process.StandardError,
            standardError,
            stopOutputPumps.Token);
        Task processExitTask = process.WaitForExitAsync(CancellationToken.None);
        DotnetCommandCompletion completion = createCompletion(
            processExitTask,
            standardOutputPump,
            standardErrorPump);

        DotnetCommandWaitOutcome outcome = await completion.WaitForMainTimeoutAsync(
            options.Timeout,
            cancellationToken);
        if (outcome != DotnetCommandWaitOutcome.Completed)
        {
            Exception? terminationFailure = null;
            try
            {
                terminateProcessTree(process);
            }
            catch (Exception exception)
            {
                terminationFailure = exception;
            }

            DotnetCommandCleanupResult cleanupResult = await completion
                .WaitForCleanupGraceAsync(CleanupGracePeriod);
            Exception? graceTimeoutFailure = null;
            Exception? pumpCancellationFailure = null;
            if (cleanupResult.Outcome == DotnetCommandCleanupOutcome.TimedOut)
            {
                graceTimeoutFailure = new TimeoutException(
                    $"Process exit/output drain did not complete within the {CleanupGracePeriod} cleanup grace period.");
                try
                {
                    stopOutputPumps.Cancel();
                }
                catch (Exception exception)
                {
                    pumpCancellationFailure = exception;
                }
                finally
                {
                    completion.ObserveIncompleteFault();
                }
            }

            stopwatch.Stop();
            string verb = GetCommandVerb(options.Arguments);
            string cleanupStatus = terminationFailure is null
                ? string.Empty
                : $"{Environment.NewLine}Process-tree termination failed during cleanup.";
            cleanupStatus += cleanupResult.Outcome switch
            {
                DotnetCommandCleanupOutcome.TimedOut =>
                    $"{Environment.NewLine}Process exit/output drain did not complete within the {CleanupGracePeriod} cleanup grace period.",
                DotnetCommandCleanupOutcome.Faulted =>
                    $"{Environment.NewLine}Process exit/output drain failed during cleanup.",
                _ => string.Empty
            };
            cleanupStatus += pumpCancellationFailure is null
                ? string.Empty
                : $"{Environment.NewLine}Output-pump cancellation failed during cleanup.";
            string diagnostics = FormatOutputDiagnostics(standardOutput, standardError);
            Exception? cleanupFailure = CombineCleanupFailures(
                terminationFailure,
                cleanupResult.Failure,
                graceTimeoutFailure,
                pumpCancellationFailure);

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    $"The dotnet '{verb}' command was canceled by the caller."
                    + cleanupStatus
                    + diagnostics,
                    cleanupFailure,
                    cancellationToken);
            }

            throw new TimeoutException(
                $"The dotnet '{verb}' command exceeded its {options.Timeout} timeout."
                + cleanupStatus
                + diagnostics,
                cleanupFailure);
        }

        stopwatch.Stop();
        string capturedStandardOutput = standardOutput.GetText();
        string capturedStandardError = standardError.GetText();
        string completedVerb = GetCommandVerb(options.Arguments);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The dotnet '{completedVerb}' command exited with code {process.ExitCode}."
                + FormatOutputDiagnostics(standardOutput, standardError));
        }

        return new DotnetCommandResult(
            process.ExitCode,
            capturedStandardOutput,
            capturedStandardError,
            stopwatch.Elapsed);
    }

    private static async Task PumpOutputAsync(
        StreamReader reader,
        BoundedTextAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        char[] buffer = ArrayPool<char>.Shared.Rent(OutputReadBufferCharacters);
        try
        {
            while (true)
            {
                int charactersRead = await reader.ReadAsync(
                    buffer.AsMemory(0, OutputReadBufferCharacters),
                    cancellationToken);
                if (charactersRead == 0)
                {
                    return;
                }

                accumulator.Append(buffer.AsSpan(0, charactersRead));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
        }
    }

    private static Exception? CombineCleanupFailures(params Exception?[] failures)
    {
        var cleanupFailures = new List<Exception>();
        foreach (Exception? failure in failures)
        {
            if (failure is not null)
            {
                cleanupFailures.Add(failure);
            }
        }

        return cleanupFailures.Count switch
        {
            0 => null,
            1 => cleanupFailures[0],
            _ => new AggregateException("Multiple dotnet command cleanup operations failed.", cleanupFailures)
        };
    }

    private static string GetCommandVerb(IReadOnlyList<string> arguments)
    {
        return arguments.FirstOrDefault() ?? "<unknown>";
    }

    internal static string FormatOutputDiagnostics(
        BoundedTextAccumulator standardOutput,
        BoundedTextAccumulator standardError)
    {
        return $"{Environment.NewLine}stdout:{Environment.NewLine}{standardOutput.GetText()}"
            + $"{Environment.NewLine}stderr:{Environment.NewLine}{standardError.GetText()}";
    }
}
