using System.Buffers;
using System.Diagnostics;
using System.Text;

namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

internal enum ChildProcessStream
{
    StandardOutput,
    StandardError
}

internal sealed record ChildProcessOutputLine(
    long Sequence,
    DateTimeOffset TimestampUtc,
    ChildProcessStream Stream,
    string Text);

internal sealed record ChildProcessStartOptions(
    string Name,
    string FileName,
    string WorkingDirectory,
    string LogFilePath,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment);

internal sealed record ChildProcessExit(
    int ExitCode,
    IReadOnlyList<ChildProcessOutputLine> Output);

internal sealed record ChildProcessOutputSnapshot(
    IReadOnlyList<ChildProcessOutputLine> Lines,
    int StandardOutputRetainedCharacters,
    int StandardErrorRetainedCharacters,
    bool StandardOutputTruncated,
    bool StandardErrorTruncated);

internal sealed record RetainedChildProcessLine(string Text, bool Truncated);

internal sealed class BoundedTrailingLineAccumulator
{
    private readonly object sync = new();
    private readonly char[] retainedCharacters;
    private int startIndex;
    private int characterCount;
    private bool truncated;

    internal BoundedTrailingLineAccumulator(int maximumRetainedCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRetainedCharacters);
        retainedCharacters = new char[maximumRetainedCharacters];
    }

    internal int CharacterCount
    {
        get
        {
            lock (sync)
            {
                return characterCount;
            }
        }
    }

    internal bool Truncated
    {
        get
        {
            lock (sync)
            {
                return truncated;
            }
        }
    }

    internal void Append(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return;
        }

        lock (sync)
        {
            if (value.Length >= retainedCharacters.Length)
            {
                truncated = truncated
                    || characterCount > 0
                    || value.Length > retainedCharacters.Length;
                value[^retainedCharacters.Length..].CopyTo(retainedCharacters);
                startIndex = 0;
                characterCount = retainedCharacters.Length;
                return;
            }

            int overflow = Math.Max(
                0,
                characterCount + value.Length - retainedCharacters.Length);
            if (overflow > 0)
            {
                startIndex = (startIndex + overflow) % retainedCharacters.Length;
                characterCount -= overflow;
                truncated = true;
            }

            int writeIndex = (startIndex + characterCount) % retainedCharacters.Length;
            int firstCopyLength = Math.Min(value.Length, retainedCharacters.Length - writeIndex);
            value[..firstCopyLength].CopyTo(retainedCharacters.AsSpan(writeIndex));
            value[firstCopyLength..].CopyTo(retainedCharacters);
            characterCount += value.Length;
        }
    }

    internal RetainedChildProcessLine TakeLine()
    {
        lock (sync)
        {
            var lineCharacters = new char[characterCount];
            int firstCopyLength = Math.Min(
                characterCount,
                retainedCharacters.Length - startIndex);
            retainedCharacters
                .AsSpan(startIndex, firstCopyLength)
                .CopyTo(lineCharacters);
            retainedCharacters
                .AsSpan(0, characterCount - firstCopyLength)
                .CopyTo(lineCharacters.AsSpan(firstCopyLength));

            var line = new RetainedChildProcessLine(new string(lineCharacters), truncated);
            startIndex = 0;
            characterCount = 0;
            truncated = false;
            return line;
        }
    }
}

internal sealed class ChildProcessOutputPump
{
    private const int ReadBufferCharacterCount = 4 * 1024;
    private readonly TextReader reader;
    private readonly ChildProcessStream stream;
    private readonly ChildProcessOutputBuffer output;
    private readonly Action publishLine;

    internal ChildProcessOutputPump(
        TextReader reader,
        ChildProcessStream stream,
        ChildProcessOutputBuffer output,
        Action publishLine)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(publishLine);
        this.reader = reader;
        this.stream = stream;
        this.output = output;
        this.publishLine = publishLine;
    }

    internal int RetainedPartialCharacterCount => output.GetPartialCharacterCount(stream);

    internal bool PartialLineTruncated => output.IsPartialLineTruncated(stream);

    internal async Task RunAsync()
    {
        char[] readBuffer = ArrayPool<char>.Shared.Rent(ReadBufferCharacterCount);
        bool swallowLineFeed = false;

        try
        {
            while (true)
            {
                int charactersRead = await reader.ReadAsync(
                    readBuffer.AsMemory(0, ReadBufferCharacterCount),
                    CancellationToken.None);
                if (charactersRead == 0)
                {
                    if (output.HasPartialLine(stream))
                    {
                        PublishPartialLine();
                    }

                    return;
                }

                int segmentStart = 0;
                for (int index = 0; index < charactersRead; index++)
                {
                    char character = readBuffer[index];
                    if (swallowLineFeed)
                    {
                        swallowLineFeed = false;
                        if (character == '\n')
                        {
                            segmentStart = index + 1;
                            continue;
                        }
                    }

                    if (character is not ('\r' or '\n'))
                    {
                        continue;
                    }

                    output.AppendPartial(
                        stream,
                        readBuffer.AsSpan(segmentStart, index - segmentStart));
                    PublishPartialLine();
                    segmentStart = index + 1;
                    swallowLineFeed = character == '\r';
                }

                output.AppendPartial(
                    stream,
                    readBuffer.AsSpan(segmentStart, charactersRead - segmentStart));
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(readBuffer);
        }
    }

    private void PublishPartialLine()
    {
        publishLine();
    }
}

internal sealed class ChildProcessOutputBuffer
{
    private readonly object sync = new();
    private readonly int maximumRetainedCharactersPerStream;
    private readonly List<ChildProcessOutputLine> lines = [];
    private readonly BoundedTrailingLineAccumulator standardOutputPartialLine;
    private readonly BoundedTrailingLineAccumulator standardErrorPartialLine;
    private int standardOutputCompletedRetainedCharacters;
    private int standardErrorCompletedRetainedCharacters;
    private bool standardOutputTruncated;
    private bool standardErrorTruncated;

    internal ChildProcessOutputBuffer(int maximumRetainedCharactersPerStream)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRetainedCharactersPerStream);
        this.maximumRetainedCharactersPerStream = maximumRetainedCharactersPerStream;
        standardOutputPartialLine = new(maximumRetainedCharactersPerStream);
        standardErrorPartialLine = new(maximumRetainedCharactersPerStream);
    }

    internal void Append(ChildProcessOutputLine line, bool linePrefixTruncated = false)
    {
        lock (sync)
        {
            if (linePrefixTruncated)
            {
                SetTruncated(line.Stream);
            }

            lines.Add(line);
            AddCompletedRetainedCharacters(line.Stream, GetRetentionCost(line.Text));
            TrimStream(
                line.Stream,
                maximumRetainedCharactersPerStream - GetPartialLine(line.Stream).CharacterCount);
        }
    }

    internal void AppendPartial(ChildProcessStream stream, ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return;
        }

        lock (sync)
        {
            BoundedTrailingLineAccumulator partialLine = GetPartialLine(stream);
            int retainedAfterAppend = Math.Min(
                maximumRetainedCharactersPerStream,
                partialLine.CharacterCount + value.Length);

            TrimStream(stream, maximumRetainedCharactersPerStream - retainedAfterAppend);
            partialLine.Append(value);
            if (partialLine.Truncated)
            {
                SetTruncated(stream);
            }
        }
    }

    internal bool HasPartialLine(ChildProcessStream stream)
    {
        lock (sync)
        {
            BoundedTrailingLineAccumulator partialLine = GetPartialLine(stream);
            return partialLine.CharacterCount > 0 || partialLine.Truncated;
        }
    }

    internal int GetPartialCharacterCount(ChildProcessStream stream)
    {
        lock (sync)
        {
            return GetPartialLine(stream).CharacterCount;
        }
    }

    internal bool IsPartialLineTruncated(ChildProcessStream stream)
    {
        lock (sync)
        {
            return GetPartialLine(stream).Truncated;
        }
    }

    internal ChildProcessOutputLine PublishPartialLine(
        long sequence,
        DateTimeOffset timestampUtc,
        ChildProcessStream stream)
    {
        lock (sync)
        {
            RetainedChildProcessLine retainedLine = GetPartialLine(stream).TakeLine();
            if (retainedLine.Truncated)
            {
                SetTruncated(stream);
            }

            var line = new ChildProcessOutputLine(
                sequence,
                timestampUtc,
                stream,
                retainedLine.Text);
            lines.Add(line);
            AddCompletedRetainedCharacters(stream, GetRetentionCost(line.Text));
            TrimStream(stream, maximumRetainedCharactersPerStream);
            return line;
        }
    }

    internal ChildProcessOutputSnapshot Snapshot()
    {
        lock (sync)
        {
            return new ChildProcessOutputSnapshot(
                lines.ToArray(),
                standardOutputCompletedRetainedCharacters
                    + standardOutputPartialLine.CharacterCount,
                standardErrorCompletedRetainedCharacters
                    + standardErrorPartialLine.CharacterCount,
                standardOutputTruncated,
                standardErrorTruncated);
        }
    }

    private void TrimStream(ChildProcessStream stream, int maximumCompletedCharacters)
    {
        while (GetCompletedRetainedCharacters(stream) > maximumCompletedCharacters)
        {
            int firstStreamLineIndex = lines.FindIndex(line => line.Stream == stream);
            ChildProcessOutputLine firstStreamLine = lines[firstStreamLineIndex];
            int excessCharacters =
                GetCompletedRetainedCharacters(stream) - maximumCompletedCharacters;
            int lineCost = GetRetentionCost(firstStreamLine.Text);

            SetTruncated(stream);
            if (lineCost <= excessCharacters)
            {
                lines.RemoveAt(firstStreamLineIndex);
                AddCompletedRetainedCharacters(stream, -lineCost);
                continue;
            }

            lines[firstStreamLineIndex] = firstStreamLine with
            {
                Text = firstStreamLine.Text[excessCharacters..]
            };
            AddCompletedRetainedCharacters(stream, -excessCharacters);
        }
    }

    private BoundedTrailingLineAccumulator GetPartialLine(ChildProcessStream stream) =>
        stream == ChildProcessStream.StandardOutput
            ? standardOutputPartialLine
            : standardErrorPartialLine;

    private int GetCompletedRetainedCharacters(ChildProcessStream stream) =>
        stream == ChildProcessStream.StandardOutput
            ? standardOutputCompletedRetainedCharacters
            : standardErrorCompletedRetainedCharacters;

    private void AddCompletedRetainedCharacters(
        ChildProcessStream stream,
        int characterCount)
    {
        if (stream == ChildProcessStream.StandardOutput)
        {
            standardOutputCompletedRetainedCharacters += characterCount;
        }
        else
        {
            standardErrorCompletedRetainedCharacters += characterCount;
        }
    }

    private void SetTruncated(ChildProcessStream stream)
    {
        if (stream == ChildProcessStream.StandardOutput)
        {
            standardOutputTruncated = true;
        }
        else
        {
            standardErrorTruncated = true;
        }
    }

    private static int GetRetentionCost(string text) => Math.Max(1, text.Length);
}

internal sealed class ChildProcess : IAsyncDisposable
{
    private const int MaximumRetainedCharactersPerStream = 64 * 1024;
    private static readonly TimeSpan TerminationTimeout = TimeSpan.FromSeconds(10);
    private readonly object outputGate = new();
    private readonly object lifecycleGate = new();
    private readonly string name;
    private readonly Process process;
    private readonly StreamWriter logWriter;
    private readonly ChildProcessOutputBuffer output = new(MaximumRetainedCharactersPerStream);
    private readonly Task processExitTask;
    private readonly Task standardOutputPump;
    private readonly Task standardErrorPump;
    private readonly Task completionTask;
    private TaskCompletionSource outputChanged = CreateOutputChangedSource();
    private Task? stopTask;
    private Task? disposeTask;
    private long nextSequence;

    private ChildProcess(string name, Process process, StreamWriter logWriter)
    {
        this.name = name;
        this.process = process;
        this.logWriter = logWriter;

        standardOutputPump = new ChildProcessOutputPump(
            process.StandardOutput,
            ChildProcessStream.StandardOutput,
            output,
            () => RecordOutput(ChildProcessStream.StandardOutput)).RunAsync();
        standardErrorPump = new ChildProcessOutputPump(
            process.StandardError,
            ChildProcessStream.StandardError,
            output,
            () => RecordOutput(ChildProcessStream.StandardError)).RunAsync();
        processExitTask = process.WaitForExitAsync();
        completionTask = CompleteAsync();
    }

    internal int ProcessId => process.Id;

    internal string Name => name;

    internal bool HasExited => process.HasExited;

    internal int ExitCode => process.ExitCode;

    internal Task WaitForExitSignalAsync() => processExitTask;

    internal async Task<bool> WaitForAddressInUseOutputAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await WaitForOutputAsync(
                "address-in-use classification",
                IsAddressInUseOutput,
                timeout,
                cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (InvalidOperationException) when (HasExited)
        {
            return SnapshotOutput().Any(IsAddressInUseOutput);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static bool IsAddressInUseOutput(ChildProcessOutputLine line) =>
        line.Text.Contains("AddressInUseException", StringComparison.OrdinalIgnoreCase)
        || line.Text.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
        || line.Text.Contains(
            "Only one usage of each socket address",
            StringComparison.OrdinalIgnoreCase);

    internal static ChildProcess Start(ChildProcessStartOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var startInfo = new ProcessStartInfo
        {
            FileName = options.FileName,
            WorkingDirectory = options.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Environment.Clear();

        foreach (string argument in options.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach ((string key, string value) in options.Environment)
        {
            startInfo.Environment.Add(key, value);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.LogFilePath))!);
        var logWriter = new StreamWriter(
            new FileStream(
                options.LogFilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
        var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Process '{options.Name}' did not start.");
            }

            return new ChildProcess(options.Name, process, logWriter);
        }
        catch
        {
            process.Dispose();
            logWriter.Dispose();
            throw;
        }
    }

    internal IReadOnlyList<ChildProcessOutputLine> SnapshotOutput()
    {
        lock (outputGate)
        {
            return output.Snapshot().Lines;
        }
    }

    internal async Task<ChildProcessOutputLine> WaitForOutputAsync(
        string purpose,
        Func<ChildProcessOutputLine, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentNullException.ThrowIfNull(predicate);

        var stopwatch = Stopwatch.StartNew();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            (ChildProcessOutputLine[] snapshot, Task changeTask) = CaptureOutputState();
            ChildProcessOutputLine? match = snapshot.FirstOrDefault(predicate);
            if (match is not null)
            {
                return match;
            }

            if (processExitTask.IsCompleted)
            {
                await AwaitExitedProcessAsync(cancellationToken);
                return FindOutputOrThrowForExit(purpose, predicate);
            }

            TimeSpan remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw CreateOutputTimeoutException(purpose, timeout);
            }

            Task timeoutTask = Task.Delay(remaining, cancellationToken);
            Task completed = await Task.WhenAny(changeTask, processExitTask, timeoutTask);
            cancellationToken.ThrowIfCancellationRequested();

            if (completed == changeTask)
            {
                continue;
            }

            if (completed == processExitTask)
            {
                await AwaitExitedProcessAsync(cancellationToken);
                return FindOutputOrThrowForExit(purpose, predicate);
            }

            (snapshot, _) = CaptureOutputState();
            match = snapshot.FirstOrDefault(predicate);
            if (match is not null)
            {
                return match;
            }

            if (processExitTask.IsCompleted)
            {
                await AwaitExitedProcessAsync(cancellationToken);
                return FindOutputOrThrowForExit(purpose, predicate);
            }

            throw CreateOutputTimeoutException(purpose, timeout);
        }
    }

    internal async Task<ChildProcessExit> WaitForExitAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await completionTask.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                FormatLifecycleDiagnostics(
                    name,
                    $"exit and output drain within {timeout}",
                    CaptureOutputSnapshot()),
                exception);
        }

        return new ChildProcessExit(process.ExitCode, SnapshotOutput());
    }

    internal async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task ownedStopTask;
        lock (lifecycleGate)
        {
            stopTask ??= StopCoreAsync();
            ownedStopTask = stopTask;
        }

        await ownedStopTask.WaitAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        lock (lifecycleGate)
        {
            disposeTask ??= DisposeCoreAsync();
            return new ValueTask(disposeTask);
        }
    }

    private static TaskCompletionSource CreateOutputChangedSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void RecordOutput(ChildProcessStream stream)
    {
        lock (outputGate)
        {
            output.PublishPartialLine(
                ++nextSequence,
                DateTimeOffset.UtcNow,
                stream);

            TaskCompletionSource completedChange = outputChanged;
            outputChanged = CreateOutputChangedSource();
            completedChange.TrySetResult();
        }
    }

    private async Task CompleteAsync()
    {
        await processExitTask;
        await Task.WhenAll(standardOutputPump, standardErrorPump);
        WriteRetainedLog();
    }

    private (ChildProcessOutputLine[] Output, Task ChangeTask) CaptureOutputState()
    {
        lock (outputGate)
        {
            return (output.Snapshot().Lines.ToArray(), outputChanged.Task);
        }
    }

    private async Task AwaitExitedProcessAsync(CancellationToken cancellationToken)
    {
        try
        {
            await completionTask.WaitAsync(TerminationTimeout, cancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                FormatExitDiagnostics(
                    name,
                    process.ExitCode,
                    $"output drain within {TerminationTimeout}",
                    CaptureOutputSnapshot()),
                exception);
        }
    }

    private ChildProcessOutputLine FindOutputOrThrowForExit(
        string purpose,
        Func<ChildProcessOutputLine, bool> predicate)
    {
        ChildProcessOutputSnapshot capturedOutput = CaptureOutputSnapshot();
        ChildProcessOutputLine? match = capturedOutput.Lines.FirstOrDefault(predicate);
        if (match is not null)
        {
            return match;
        }

        throw new InvalidOperationException(FormatExitDiagnostics(
            name,
            process.ExitCode,
            purpose,
            capturedOutput));
    }

    private TimeoutException CreateOutputTimeoutException(string purpose, TimeSpan timeout) =>
        new(FormatLifecycleDiagnostics(
            name,
            $"{purpose} within {timeout}",
            CaptureOutputSnapshot()));

    private async Task StopCoreAsync()
    {
        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
            }
        }

        try
        {
            await completionTask.WaitAsync(TerminationTimeout);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                FormatLifecycleDiagnostics(
                    name,
                    $"termination and output drain within {TerminationTimeout}",
                    CaptureOutputSnapshot()),
                exception);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await StopAsync();
        process.Dispose();
        logWriter.Dispose();
    }

    private static string GetStreamLabel(ChildProcessStream stream) =>
        stream == ChildProcessStream.StandardOutput ? "stdout" : "stderr";

    private ChildProcessOutputSnapshot CaptureOutputSnapshot()
    {
        lock (outputGate)
        {
            return output.Snapshot();
        }
    }

    private void WriteRetainedLog()
    {
        ChildProcessOutputSnapshot snapshot = CaptureOutputSnapshot();
        logWriter.WriteLine(
            $"[metadata] stdout-retained-characters={snapshot.StandardOutputRetainedCharacters};"
            + $" stdout-truncated={snapshot.StandardOutputTruncated};"
            + $" stderr-retained-characters={snapshot.StandardErrorRetainedCharacters};"
            + $" stderr-truncated={snapshot.StandardErrorTruncated}");

        foreach (ChildProcessOutputLine line in snapshot.Lines)
        {
            logWriter.WriteLine($"[{GetStreamLabel(line.Stream)}] {line.Text}");
        }
    }

    internal static string FormatExitDiagnostics(
        string processName,
        int exitCode,
        string lifecyclePhase,
        ChildProcessOutputSnapshot snapshot) =>
        $"Process '{processName}' exited with code {exitCode} during {lifecyclePhase}. "
        + FormatOutputSummary(snapshot);

    private static string FormatLifecycleDiagnostics(
        string processName,
        string lifecyclePhase,
        ChildProcessOutputSnapshot snapshot) =>
        $"Process '{processName}' did not complete {lifecyclePhase}. "
        + FormatOutputSummary(snapshot);

    private static string FormatOutputSummary(ChildProcessOutputSnapshot snapshot) =>
        $"Standard output retained characters: {snapshot.StandardOutputRetainedCharacters}; "
        + $"truncated: {snapshot.StandardOutputTruncated}. "
        + $"Standard error retained characters: {snapshot.StandardErrorRetainedCharacters}; "
        + $"truncated: {snapshot.StandardErrorTruncated}.";
}
