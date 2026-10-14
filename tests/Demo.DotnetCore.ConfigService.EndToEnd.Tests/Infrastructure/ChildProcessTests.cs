namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

public sealed class ChildProcessTests
{
    [Fact]
    public async Task Pump_shares_the_stream_cap_between_completed_output_and_a_live_partial_line()
    {
        const int maximumRetainedCharacters = 64 * 1024;
        string completedLine = new('c', maximumRetainedCharacters);
        string partialLine = new('p', maximumRetainedCharacters);
        var reader = new PausedEofTextReader(completedLine + '\n' + partialLine);
        var output = new ChildProcessOutputBuffer(maximumRetainedCharacters);
        output.Append(new ChildProcessOutputLine(
            1,
            DateTimeOffset.UnixEpoch,
            ChildProcessStream.StandardError,
            new string('e', maximumRetainedCharacters)));
        long nextSequence = 1;
        var pump = new ChildProcessOutputPump(
            reader,
            ChildProcessStream.StandardOutput,
            output,
            () =>
            {
                output.PublishPartialLine(
                    ++nextSequence,
                    DateTimeOffset.UnixEpoch,
                    ChildProcessStream.StandardOutput);
            });

        Task pumping = pump.RunAsync();
        await reader.WaitUntilWaitingForEofAsync().WaitAsync(TimeSpan.FromSeconds(2));

        ChildProcessOutputSnapshot beforeEof = output.Snapshot();
        int completedRetainedCharacters = beforeEof.Lines
            .Where(static line => line.Stream == ChildProcessStream.StandardOutput)
            .Sum(static line => Math.Max(1, line.Text.Length));
        int partialRetainedCharacters = pump.RetainedPartialCharacterCount;

        reader.PublishEof();
        await pumping.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.InRange(
            completedRetainedCharacters + partialRetainedCharacters,
            0,
            maximumRetainedCharacters);
        Assert.Equal(
            completedRetainedCharacters + partialRetainedCharacters,
            beforeEof.StandardOutputRetainedCharacters);
        ChildProcessOutputLine independentErrorLine = Assert.Single(beforeEof.Lines);
        Assert.Equal(ChildProcessStream.StandardError, independentErrorLine.Stream);
        Assert.Equal(maximumRetainedCharacters, independentErrorLine.Text.Length);
        Assert.True(beforeEof.StandardOutputTruncated);
        Assert.False(beforeEof.StandardErrorTruncated);
        Assert.Equal(
            maximumRetainedCharacters,
            beforeEof.StandardErrorRetainedCharacters);

        ChildProcessOutputSnapshot afterEof = output.Snapshot();
        ChildProcessOutputLine retainedLine = Assert.Single(
            afterEof.Lines,
            static line => line.Stream == ChildProcessStream.StandardOutput);
        Assert.Equal(partialLine, retainedLine.Text);
        Assert.Equal(maximumRetainedCharacters, afterEof.StandardOutputRetainedCharacters);
        Assert.True(afterEof.StandardOutputTruncated);
    }

    [Fact]
    public async Task Pump_bounds_an_oversized_unterminated_line_before_eof_and_publishes_its_tail()
    {
        const int maximumRetainedCharacters = 64 * 1024;
        string input = "discarded-prefix"
            + new string('x', maximumRetainedCharacters)
            + "retained-eof-tail";
        var reader = new PausedEofTextReader(input);
        var output = new ChildProcessOutputBuffer(maximumRetainedCharacters);
        long nextSequence = 0;
        var pump = new ChildProcessOutputPump(
            reader,
            ChildProcessStream.StandardOutput,
            output,
            () =>
            {
                output.PublishPartialLine(
                    ++nextSequence,
                    DateTimeOffset.UnixEpoch,
                    ChildProcessStream.StandardOutput);
            });

        Task pumping = pump.RunAsync();
        await reader.WaitUntilWaitingForEofAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.InRange(pump.RetainedPartialCharacterCount, 0, maximumRetainedCharacters);
        Assert.True(pump.PartialLineTruncated);
        Assert.Empty(output.Snapshot().Lines);

        reader.PublishEof();
        await pumping.WaitAsync(TimeSpan.FromSeconds(2));

        ChildProcessOutputSnapshot snapshot = output.Snapshot();
        ChildProcessOutputLine retainedLine = Assert.Single(snapshot.Lines);
        Assert.Equal(input[^maximumRetainedCharacters..], retainedLine.Text);
        Assert.True(snapshot.StandardOutputTruncated);
    }

    [Fact]
    public async Task Pump_preserves_completed_line_and_crlf_behavior()
    {
        var output = new ChildProcessOutputBuffer(64 * 1024);
        long nextSequence = 0;
        var pump = new ChildProcessOutputPump(
            new StringReader("first\r\nsecond\nthird\rfourth"),
            ChildProcessStream.StandardOutput,
            output,
            () =>
            {
                output.PublishPartialLine(
                    ++nextSequence,
                    DateTimeOffset.UnixEpoch,
                    ChildProcessStream.StandardOutput);
            });

        await pump.RunAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(
        [
            "first",
            "second",
            "third",
            "fourth"
        ],
            output.Snapshot().Lines.Select(static line => line.Text));
        Assert.False(output.Snapshot().StandardOutputTruncated);
    }

    [Fact]
    public void Output_is_bounded_per_stream_and_exit_diagnostics_never_include_raw_lines()
    {
        const int maximumRetainedCharacters = 64 * 1024;
        const string retainedSensitiveValue = "retained-sensitive-configuration-value";
        var output = new ChildProcessOutputBuffer(maximumRetainedCharacters);
        output.Append(new ChildProcessOutputLine(
            1,
            DateTimeOffset.UnixEpoch,
            ChildProcessStream.StandardOutput,
            "discarded-sensitive-prefix" + new string('o', maximumRetainedCharacters) + retainedSensitiveValue));
        output.Append(new ChildProcessOutputLine(
            2,
            DateTimeOffset.UnixEpoch,
            ChildProcessStream.StandardError,
            "discarded-sensitive-error" + new string('e', maximumRetainedCharacters) + retainedSensitiveValue));

        ChildProcessOutputSnapshot snapshot = output.Snapshot();
        string retainedRawOutput = string.Join(
            Environment.NewLine,
            snapshot.Lines.Select(static line => line.Text));
        string diagnostics = ChildProcess.FormatExitDiagnostics(
            "Demo process",
            17,
            "post-readiness proof request",
            snapshot);

        Assert.InRange(snapshot.StandardOutputRetainedCharacters, 0, maximumRetainedCharacters);
        Assert.InRange(snapshot.StandardErrorRetainedCharacters, 0, maximumRetainedCharacters);
        Assert.True(snapshot.StandardOutputTruncated);
        Assert.True(snapshot.StandardErrorTruncated);
        Assert.Contains(retainedSensitiveValue, retainedRawOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(retainedSensitiveValue, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("discarded-sensitive", diagnostics, StringComparison.Ordinal);
        Assert.Contains("Demo process", diagnostics, StringComparison.Ordinal);
        Assert.Contains("code 17", diagnostics, StringComparison.Ordinal);
        Assert.Contains("post-readiness proof request", diagnostics, StringComparison.Ordinal);
        Assert.Contains(maximumRetainedCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture), diagnostics);
    }

    private sealed class PausedEofTextReader(string text) : TextReader
    {
        private readonly TaskCompletionSource publishEof =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource waitingForEof =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int position;

        internal Task WaitUntilWaitingForEofAsync() => waitingForEof.Task;

        internal void PublishEof() => publishEof.TrySetResult();

        public override ValueTask<int> ReadAsync(
            Memory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            if (position < text.Length)
            {
                int characterCount = Math.Min(buffer.Length, text.Length - position);
                text.AsMemory(position, characterCount).CopyTo(buffer);
                position += characterCount;
                return ValueTask.FromResult(characterCount);
            }

            waitingForEof.TrySetResult();
            return new ValueTask<int>(WaitForEofAsync(cancellationToken));
        }

        private async Task<int> WaitForEofAsync(CancellationToken cancellationToken)
        {
            await publishEof.Task.WaitAsync(cancellationToken);
            return 0;
        }
    }
}
