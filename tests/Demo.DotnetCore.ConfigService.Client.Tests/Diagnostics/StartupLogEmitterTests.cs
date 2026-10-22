using System.Net;
using Demo.DotnetCore.ConfigService.Client.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Demo.DotnetCore.ConfigService.Client.Tests.Diagnostics;

public sealed class StartupLogEmitterTests
{
    [Fact]
    public void Emits_one_structured_event_for_each_requested_lifecycle_event()
    {
        var logger = new RecordingLogger();
        var emitter = new StartupLogEmitter(logger);
        var failure = new CentralConfigLoadException(
            CentralConfigFailureKind.MissingRequiredKeys,
            2,
            TimeSpan.FromMilliseconds(250),
            HttpStatusCode.OK,
            "UAT",
            ["Demo:Required"]);

        emitter.TryInformation(2, 1, 2, TimeSpan.FromMilliseconds(200));
        emitter.TryWarning(1, CentralConfigFailureKind.TransportFailure, null, TimeSpan.FromSeconds(1));
        emitter.TryError(failure);

        Assert.Collection(
            logger.Entries,
            entry => Assert.Equal(LogLevel.Information, entry.Level),
            entry => Assert.Equal(LogLevel.Warning, entry.Level),
            entry =>
            {
                Assert.Equal(LogLevel.Error, entry.Level);
                Assert.Same(failure, entry.Exception);
            });
        Assert.Equal("Central configuration loaded: {ValueCount} values, {MissingFallbackKeyCount} fallback-missing keys, {AttemptCount} attempt(s), {ElapsedMs} ms.", logger.Entries[0].OriginalFormat);
        Assert.Equal("Central configuration retry scheduled after attempt {AttemptCount}: {FailureKind}; HTTP status {HttpStatusCode}; delay {DelayMs} ms; central error code {CentralErrorCode}; central trace id {CentralTraceId}.", logger.Entries[1].OriginalFormat);
        Assert.Equal("Central configuration load failed: {FailureKind}; attempts {AttemptCount}; elapsed {ElapsedMs} ms; Environment {Environment}; missing required keys {MissingRequiredKeys}; central error code {CentralErrorCode}; central trace id {CentralTraceId}.", logger.Entries[2].OriginalFormat);
        AssertStructuredFields(
            logger.Entries[0],
            ("ValueCount", 2),
            ("MissingFallbackKeyCount", 1),
            ("AttemptCount", 2),
            ("ElapsedMs", 200d));
        AssertStructuredFields(
            logger.Entries[1],
            ("AttemptCount", 1),
            ("FailureKind", CentralConfigFailureKind.TransportFailure),
            ("HttpStatusCode", null),
            ("DelayMs", 1000d),
            ("CentralErrorCode", null),
            ("CentralTraceId", null));
        AssertStructuredFields(
            logger.Entries[2],
            ("FailureKind", CentralConfigFailureKind.MissingRequiredKeys),
            ("AttemptCount", 2),
            ("ElapsedMs", 250d),
            ("Environment", "UAT"),
            ("MissingRequiredKeys", failure.MissingRequiredKeys),
            ("CentralErrorCode", null),
            ("CentralTraceId", null));
        IReadOnlyList<string> missingRequiredKeys = Assert.IsAssignableFrom<IReadOnlyList<string>>(
            logger.Entries[2].Fields[4].Value);
        Assert.Equal(["Demo:Required"], missingRequiredKeys);
    }

    [Fact]
    public void Retry_warning_carries_the_validated_central_problem_reference()
    {
        var logger = new RecordingLogger();
        var emitter = new StartupLogEmitter(logger);

        emitter.TryWarning(
            2,
            CentralConfigFailureKind.HttpFailure,
            HttpStatusCode.InternalServerError,
            TimeSpan.FromSeconds(2),
            new CentralProblemReference("persistence_failure", "0HN1RETRY:00000002"));

        AssertStructuredFields(
            Assert.Single(logger.Entries),
            ("AttemptCount", 2),
            ("FailureKind", CentralConfigFailureKind.HttpFailure),
            ("HttpStatusCode", 500),
            ("DelayMs", 2000d),
            ("CentralErrorCode", "persistence_failure"),
            ("CentralTraceId", "0HN1RETRY:00000002"));
    }

    [Fact]
    public void Unknown_response_member_warning_lists_names_only()
    {
        var logger = new RecordingLogger();
        var emitter = new StartupLogEmitter(logger);
        string[] names = ["revision", "future"];

        emitter.TryUnknownResponseMembers(names);

        LogEntry entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("Central configuration response contains members this Client version does not recognize: {UnknownResponseMembers}. They were ignored; consider upgrading the Client package.", entry.OriginalFormat);
        AssertStructuredFields(entry, ("UnknownResponseMembers", names));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Logger_exceptions_never_escape(bool throwFromIsEnabled)
    {
        var emitter = new StartupLogEmitter(new ThrowingLogger(throwFromIsEnabled));
        var failure = new CentralConfigLoadException(
            CentralConfigFailureKind.TransportFailure,
            4,
            TimeSpan.FromSeconds(10),
            null,
            "UAT");

        Exception? actual = Record.Exception(() =>
        {
            emitter.TryInformation(1, 0, 1, TimeSpan.Zero);
            emitter.TryWarning(1, CentralConfigFailureKind.TransportFailure, null, TimeSpan.Zero);
            emitter.TryUnknownResponseMembers(["future"]);
            emitter.TryError(failure);
        });

        Assert.Null(actual);
    }

    [Fact]
    public void Missing_logger_is_a_no_op()
    {
        var emitter = new StartupLogEmitter(null);

        Exception? actual = Record.Exception(() =>
            emitter.TryInformation(1, 0, 1, TimeSpan.Zero));

        Assert.Null(actual);
    }

    private static void AssertStructuredFields(
        LogEntry entry,
        params (string Name, object? Value)[] expectedFields)
    {
        Assert.Equal(expectedFields.Length + 1, entry.Fields.Count);

        for (int index = 0; index < expectedFields.Length; index++)
        {
            KeyValuePair<string, object?> actualField = entry.Fields[index];
            Assert.Equal(expectedFields[index].Name, actualField.Key);
            Assert.Equal(expectedFields[index].Value, actualField.Value);
        }

        KeyValuePair<string, object?> originalFormatField = entry.Fields[^1];
        Assert.Equal("{OriginalFormat}", originalFormatField.Key);
        Assert.Equal(entry.OriginalFormat, originalFormatField.Value);
    }
}
