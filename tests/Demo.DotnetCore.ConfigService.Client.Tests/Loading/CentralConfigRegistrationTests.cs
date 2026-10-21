using System.Net;
using System.Text;
using Demo.DotnetCore.ConfigService.Client.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Demo.DotnetCore.ConfigService.Client.Tests.Loading;

public sealed class CentralConfigRegistrationTests
{
    [Fact]
    public async Task Disabled_mode_is_a_true_no_op()
    {
        using var host = new TestHostBuilder(
            "{ \"CentralConfig\": { \"Enabled\": false, \"BaseAddress\": \"invalid\" } }",
            "{}");
        var transport = new FakeDistributionTransport();
        var registration = CreateRegistration(transport, new FakeTimeProvider(), new FixedRetryJitter());
        int sourceCount = host.Builder.Configuration.Sources.Count;

        await registration.RegisterAsync(host.Builder, null, CancellationToken.None);

        Assert.Equal(0, transport.CallCount);
        Assert.Equal(sourceCount, host.Builder.Configuration.Sources.Count);
    }

    [Fact]
    public async Task Invalid_bootstrap_fails_before_network_and_emits_one_terminal_error()
    {
        using var host = new TestHostBuilder("{}", "{}");
        var transport = new FakeDistributionTransport();
        var logger = new RecordingLogger();
        var registration = CreateRegistration(transport, new FakeTimeProvider(), new FixedRetryJitter());
        int sourceCount = host.Builder.Configuration.Sources.Count;

        CentralConfigLoadException exception = await Assert.ThrowsAsync<CentralConfigLoadException>(() =>
            registration.RegisterAsync(host.Builder, logger, CancellationToken.None));

        Assert.Equal(CentralConfigFailureKind.InvalidBootstrapConfiguration, exception.FailureKind);
        Assert.Equal(0, exception.AttemptCount);
        Assert.Null(exception.Environment);
        Assert.Equal(0, transport.CallCount);
        Assert.Equal(sourceCount, host.Builder.Configuration.Sources.Count);
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Inserts_after_environment_json_and_preserves_fallback_and_higher_priority_values()
    {
        const string centralValue = "central-value-must-not-enter-information-log-4e91";
        using var host = EnabledHost();
        var transport = new FakeDistributionTransport();
        transport.EnqueueResponse(Ok(
            "{\"values\":{\"Demo:Required\":\"central-value-must-not-enter-information-log-4e91\",\"Demo:Empty\":\"\"},\"missingKeys\":[\"Demo:Fallback\"]}"));
        var logger = new RecordingLogger();
        var registration = CreateRegistration(transport, new FakeTimeProvider(), new FixedRetryJitter());
        int sourceCount = host.Builder.Configuration.Sources.Count;

        await registration.RegisterAsync(host.Builder, logger, CancellationToken.None);

        Assert.Equal(centralValue, host.Builder.Configuration["Demo:Required"]);
        Assert.Equal("environment-fallback", host.Builder.Configuration["Demo:Fallback"]);
        Assert.Equal(string.Empty, host.Builder.Configuration["Demo:Empty"]);
        Assert.Equal(sourceCount + 1, host.Builder.Configuration.Sources.Count);
        Assert.True(EnvironmentJsonSourceAnchor.TryFindIndex(
            host.Builder.Configuration.Sources,
            host.Builder.Environment.EnvironmentName,
            out int anchor));
        Assert.IsType<CentralConfigSnapshotSource>(host.Builder.Configuration.Sources[anchor + 1]);
        Assert.Single(
            host.Builder.Configuration.Sources,
            source => source is CentralConfigSnapshotSource);

        host.Builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Demo:Required"] = "command-line-equivalent" });
        Assert.Equal("command-line-equivalent", host.Builder.Configuration["Demo:Required"]);
        LogEntry information = logger.Entries.Single(entry => entry.Level == LogLevel.Information);
        string informationText = string.Join(
            Environment.NewLine,
            new[] { information.Message, information.OriginalFormat }
                .Concat(information.Fields.Select(field => $"{field.Key}={field.Value}")));
        Assert.DoesNotContain(centralValue, informationText, StringComparison.Ordinal);
        Assert.DoesNotContain("Demo:Required", informationText, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Configuration_writes_after_registration_succeed_and_keep_the_central_value()
    {
        using var host = EnabledHost();
        var transport = new FakeDistributionTransport();
        transport.EnqueueResponse(Ok(
            "{\"values\":{\"Demo:Required\":\"central\",\"Demo:Empty\":\"\"},\"missingKeys\":[\"Demo:Fallback\"]}"));
        var registration = CreateRegistration(transport, new FakeTimeProvider(), new FixedRetryJitter());

        await registration.RegisterAsync(host.Builder, null, CancellationToken.None);

        // WebHost.UseUrls and UseSetting write through the indexer, which calls Set on every provider.
        host.Builder.Configuration["urls"] = "http://127.0.0.1:5299";

        Assert.Equal("http://127.0.0.1:5299", host.Builder.Configuration["urls"]);
        Assert.Equal("central", host.Builder.Configuration["Demo:Required"]);
    }

    [Fact]
    public async Task Failed_acquisition_leaves_the_pipeline_unchanged()
    {
        using var host = EnabledHost();
        var transport = new FakeDistributionTransport();
        transport.EnqueueResponse(new DistributionHttpResponse(HttpStatusCode.BadRequest, ReadOnlyMemory<byte>.Empty));
        var registration = CreateRegistration(transport, new FakeTimeProvider(), new FixedRetryJitter());
        IConfigurationSource[] before = host.Builder.Configuration.Sources.ToArray();

        CentralConfigLoadException exception = await Assert.ThrowsAsync<CentralConfigLoadException>(() =>
            registration.RegisterAsync(host.Builder, null, CancellationToken.None));

        Assert.Equal(CentralConfigFailureKind.HttpFailure, exception.FailureKind);
        Assert.Equal(before, host.Builder.Configuration.Sources);
    }

    [Fact]
    public async Task Missing_anchor_fails_before_network_as_invalid_pipeline()
    {
        using var host = EnabledHost();
        int anchor = host.Builder.Configuration.Sources
            .Select((source, index) => (source, index))
            .Single(pair => pair.source is Microsoft.Extensions.Configuration.Json.JsonConfigurationSource json
                && string.Equals(json.Path, "appsettings.UAT.json", StringComparison.OrdinalIgnoreCase))
            .index;
        host.Builder.Configuration.Sources.RemoveAt(anchor);
        var transport = new FakeDistributionTransport();
        var registration = CreateRegistration(transport, new FakeTimeProvider(), new FixedRetryJitter());

        CentralConfigLoadException exception = await Assert.ThrowsAsync<CentralConfigLoadException>(() =>
            registration.RegisterAsync(host.Builder, null, CancellationToken.None));

        Assert.Equal(CentralConfigFailureKind.InvalidConfigurationPipeline, exception.FailureKind);
        Assert.Equal("UAT", exception.Environment);
        Assert.Equal(0, exception.AttemptCount);
        Assert.Equal(0, transport.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_resolution_anchor_revalidation_rejects_missing_or_ambiguous_anchor(
        bool makeAnchorAmbiguous)
    {
        using var host = EnabledHost();
        var transport = new FakeDistributionTransport();
        TaskCompletionSource<DistributionHttpResponse> response = transport.EnqueueNonCooperativeResponse();
        var logger = new RecordingLogger();
        var registration = CreateRegistration(transport, new FakeTimeProvider(), new FixedRetryJitter());
        Task registrationTask = registration.RegisterAsync(host.Builder, logger, CancellationToken.None);
        await transport.WaitForNextCallAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(EnvironmentJsonSourceAnchor.TryFindIndex(
            host.Builder.Configuration.Sources,
            host.Builder.Environment.EnvironmentName,
            out int anchor));
        if (makeAnchorAmbiguous)
        {
            host.Builder.Configuration.Sources.Add(host.Builder.Configuration.Sources[anchor]);
        }
        else
        {
            host.Builder.Configuration.Sources.RemoveAt(anchor);
        }

        IConfigurationSource[] sourcesAfterMutation = host.Builder.Configuration.Sources.ToArray();
        response.SetResult(Ok(
            "{\"values\":{\"Demo:Required\":\"central\",\"Demo:Empty\":\"\"},\"missingKeys\":[\"Demo:Fallback\"]}"));

        CentralConfigLoadException exception = await Assert.ThrowsAsync<CentralConfigLoadException>(() =>
            registrationTask.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(CentralConfigFailureKind.InvalidConfigurationPipeline, exception.FailureKind);
        Assert.Equal(1, exception.AttemptCount);
        Assert.Equal(1, transport.CallCount);
        Assert.Equal(sourcesAfterMutation, host.Builder.Configuration.Sources);
        Assert.DoesNotContain(
            host.Builder.Configuration.Sources,
            source => source is CentralConfigSnapshotSource);
        LogEntry error = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, error.Level);
    }

    [Fact]
    public async Task Required_missing_uses_declaration_spelling_and_terminal_error_is_attempted_once()
    {
        using var host = EnabledHost();
        var transport = new FakeDistributionTransport();
        transport.EnqueueResponse(Ok(
            "{\"values\":{\"Demo:Empty\":\"\"},\"missingKeys\":[\"demo:required\",\"Demo:Fallback\"]}"));
        var logger = new RecordingLogger();
        var registration = CreateRegistration(transport, new FakeTimeProvider(), new FixedRetryJitter());

        CentralConfigLoadException exception = await Assert.ThrowsAsync<CentralConfigLoadException>(() =>
            registration.RegisterAsync(host.Builder, logger, CancellationToken.None));

        Assert.Equal(CentralConfigFailureKind.MissingRequiredKeys, exception.FailureKind);
        Assert.Equal(["Demo:Required"], exception.MissingRequiredKeys);
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_wrapped_or_logged_as_terminal_error()
    {
        using var host = EnabledHost();
        var transport = new FakeDistributionTransport();
        transport.EnqueueBlockingResponse();
        var logger = new RecordingLogger();
        var time = new FakeTimeProvider();
        var registration = CreateRegistration(transport, time, new FixedRetryJitter());
        using var caller = new CancellationTokenSource();
        Task registrationTask = registration.RegisterAsync(host.Builder, logger, caller.Token);
        await transport.WaitForNextCallAsync();

        caller.Cancel();
        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => registrationTask);

        Assert.Equal(caller.Token, exception.CancellationToken);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Theory]
    [InlineData(nameof(OwnedCancellationKind.Caller))]
    [InlineData(nameof(OwnedCancellationKind.TotalTimeout))]
    public async Task Cancellation_immediately_before_snapshot_insertion_prevents_the_single_commit(
        string cancellationName)
    {
        OwnedCancellationKind cancellationKind =
            Enum.Parse<OwnedCancellationKind>(cancellationName);
        using var host = EnabledHost();
        var transport = new FakeDistributionTransport();
        transport.EnqueueResponse(Ok(
            "{\"values\":{\"Demo:Required\":\"central\",\"Demo:Empty\":\"\"},\"missingKeys\":[\"Demo:Fallback\"]}"));
        using var snapshotCommitter = new BlockingSnapshotCommitter();
        var logger = new RecordingLogger();
        var time = new FakeTimeProvider();
        var registration = CreateRegistration(
            transport,
            time,
            new FixedRetryJitter(),
            snapshotCommitter: snapshotCommitter);
        using var caller = new CancellationTokenSource();
        int sourceCount = host.Builder.Configuration.Sources.Count;
        Task registrationTask = registration.RegisterAsync(
            host.Builder,
            logger,
            caller.Token);
        await snapshotCommitter.WaitUntilEnteredAsync();

        if (cancellationKind == OwnedCancellationKind.Caller)
        {
            caller.Cancel();
        }
        else
        {
            time.Advance(RetryingDistributionResolver.TotalTimeout);
        }

        snapshotCommitter.Release();
        Exception? observed = await Record.ExceptionAsync(() => registrationTask);

        Assert.Equal(sourceCount, host.Builder.Configuration.Sources.Count);
        Assert.DoesNotContain(
            host.Builder.Configuration.Sources,
            source => source is CentralConfigSnapshotSource);
        if (cancellationKind == OwnedCancellationKind.Caller)
        {
            OperationCanceledException exception =
                Assert.IsAssignableFrom<OperationCanceledException>(observed);
            Assert.Equal(caller.Token, exception.CancellationToken);
            Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
        }
        else
        {
            CentralConfigLoadException exception =
                Assert.IsType<CentralConfigLoadException>(observed);
            Assert.Equal(CentralConfigFailureKind.TotalTimeout, exception.FailureKind);
            Assert.Equal(1, exception.AttemptCount);
            Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        }
    }

    [Theory]
    [InlineData(nameof(OwnedCancellationKind.Caller))]
    [InlineData(nameof(OwnedCancellationKind.TotalTimeout))]
    public async Task Blocking_validation_cannot_delay_registration_cancellation_or_insert_a_snapshot(
        string cancellationName)
    {
        OwnedCancellationKind cancellationKind =
            Enum.Parse<OwnedCancellationKind>(cancellationName);
        using var host = EnabledHost();
        var transport = new FakeDistributionTransport();
        transport.EnqueueResponse(Ok("{}"));
        using var parser = new BlockingDistributionResponseParser();
        var logger = new RecordingLogger();
        var time = new FakeTimeProvider();
        var registration = CreateRegistration(
            transport,
            time,
            new FixedRetryJitter(),
            parser);
        using var caller = new CancellationTokenSource();
        int sourceCount = host.Builder.Configuration.Sources.Count;
        Task registrationTask = registration.RegisterAsync(
            host.Builder,
            logger,
            caller.Token);
        await parser.WaitUntilEnteredAsync();
        if (cancellationKind == OwnedCancellationKind.Caller)
        {
            caller.Cancel();
        }
        else
        {
            time.Advance(RetryingDistributionResolver.TotalTimeout);
        }

        Exception? observed = null;
        try
        {
            observed = await Record.ExceptionAsync(async () =>
                await registrationTask.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            parser.Release();
            await parser.WaitUntilFinishedAsync();
        }

        Assert.Equal(sourceCount, host.Builder.Configuration.Sources.Count);
        Assert.DoesNotContain(
            host.Builder.Configuration.Sources,
            source => source is CentralConfigSnapshotSource);
        if (cancellationKind == OwnedCancellationKind.Caller)
        {
            OperationCanceledException exception =
                Assert.IsAssignableFrom<OperationCanceledException>(observed);
            Assert.Equal(caller.Token, exception.CancellationToken);
            Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
        }
        else
        {
            CentralConfigLoadException exception =
                Assert.IsType<CentralConfigLoadException>(observed);
            Assert.Equal(CentralConfigFailureKind.TotalTimeout, exception.FailureKind);
            Assert.Equal(1, exception.AttemptCount);
            Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        }
    }

    [Theory]
    [InlineData(nameof(OwnedCancellationKind.Caller))]
    [InlineData(nameof(OwnedCancellationKind.TotalTimeout))]
    public async Task Blocking_snapshot_preparation_cannot_delay_cancellation_or_insert_a_source(
        string cancellationName)
    {
        const string lateFailureMarker = "late-snapshot-preparation-sensitive-marker";
        OwnedCancellationKind cancellationKind =
            Enum.Parse<OwnedCancellationKind>(cancellationName);
        using var host = EnabledHost();
        var transport = new FakeDistributionTransport();
        transport.EnqueueResponse(Ok(
            "{\"values\":{\"Demo:Required\":\"central\",\"Demo:Empty\":\"\"},\"missingKeys\":[\"Demo:Fallback\"]}"));
        using var preparer = new BlockingSnapshotSourcePreparer(
            new InvalidOperationException(lateFailureMarker));
        var logger = new RecordingLogger();
        var time = new FakeTimeProvider();
        var registration = CreateRegistration(
            transport,
            time,
            new FixedRetryJitter(),
            snapshotSourcePreparer: preparer);
        using var caller = new CancellationTokenSource();
        int sourceCount = host.Builder.Configuration.Sources.Count;
        Task registrationTask = registration.RegisterAsync(
            host.Builder,
            logger,
            caller.Token);
        await preparer.WaitUntilEnteredAsync();

        if (cancellationKind == OwnedCancellationKind.Caller)
        {
            caller.Cancel();
        }
        else
        {
            time.Advance(RetryingDistributionResolver.TotalTimeout);
        }

        Exception? promptOutcome = null;
        bool sourceWasInsertedWhileBlocked;
        try
        {
            promptOutcome = await Record.ExceptionAsync(async () =>
                await registrationTask.WaitAsync(TimeSpan.FromSeconds(2)));
            sourceWasInsertedWhileBlocked = host.Builder.Configuration.Sources
                .Any(source => source is CentralConfigSnapshotSource);
        }
        finally
        {
            preparer.Release();
            await preparer.WaitUntilFinishedAsync();
            _ = await Record.ExceptionAsync(() => registrationTask);
        }

        Assert.False(sourceWasInsertedWhileBlocked);
        Assert.Equal(sourceCount, host.Builder.Configuration.Sources.Count);
        if (cancellationKind == OwnedCancellationKind.Caller)
        {
            OperationCanceledException exception =
                Assert.IsAssignableFrom<OperationCanceledException>(promptOutcome);
            Assert.Equal(caller.Token, exception.CancellationToken);
            Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
        }
        else
        {
            CentralConfigLoadException exception =
                Assert.IsType<CentralConfigLoadException>(promptOutcome);
            Assert.Equal(CentralConfigFailureKind.TotalTimeout, exception.FailureKind);
            Assert.Equal(1, exception.AttemptCount);
            Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        }

        string diagnosticText = string.Join(
            Environment.NewLine,
            logger.Entries.SelectMany(entry =>
                new[] { entry.Message, entry.OriginalFormat }
                    .Concat(entry.Fields.Select(field => $"{field.Key}={field.Value}"))));
        Assert.DoesNotContain(lateFailureMarker, diagnosticText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(OwnedCancellationKind.Caller))]
    [InlineData(nameof(OwnedCancellationKind.TotalTimeout))]
    public void Snapshot_commit_arbiter_rejects_cancellation_claimed_before_commit(
        string cancellationName)
    {
        OwnedCancellationKind cancellationKind =
            Enum.Parse<OwnedCancellationKind>(cancellationName);
        using var caller = new CancellationTokenSource();
        using var total = new CancellationTokenSource();
        using var arbiter = new SnapshotCommitArbiter(caller.Token, total.Token);
        if (cancellationKind == OwnedCancellationKind.Caller)
        {
            caller.Cancel();
        }
        else
        {
            total.Cancel();
        }

        bool inserted = false;
        SnapshotCommitDecision decision = arbiter.TryCommit(() => inserted = true);

        Assert.False(decision.IsCommitted);
        Assert.False(inserted);
        Assert.Equal(cancellationKind, decision.Cancellation.HighestPriority);
    }

    [Fact]
    public void Snapshot_commit_arbiter_preserves_a_commit_claimed_before_later_cancellation()
    {
        using var caller = new CancellationTokenSource();
        using var total = new CancellationTokenSource();
        using var arbiter = new SnapshotCommitArbiter(caller.Token, total.Token);
        bool inserted = false;

        SnapshotCommitDecision decision = arbiter.TryCommit(() => inserted = true);
        caller.Cancel();
        total.Cancel();

        Assert.True(decision.IsCommitted);
        Assert.True(inserted);
        Assert.Equal(OwnedCancellationKind.None, decision.Cancellation.HighestPriority);
    }

    [Fact]
    public async Task Terminal_diagnostics_exclude_values_raw_bodies_credentials_and_parser_text()
    {
        string[] prohibited =
        [
            "configuration-value-65d1",
            "raw-response-body-a427",
            "Bearer credential-913c",
            "parser-message-line-7-b884"
        ];
        using var host = EnabledHost();
        var transport = new FakeDistributionTransport();
        transport.EnqueueResponse(Ok(
            """
            {
              "values": {
                "Demo:Required": "configuration-value-65d1",
                "Authorization": "Bearer credential-913c"
              },
              "missingKeys": [],
              "rawMarker": "raw-response-body-a427",
              "parserText": "parser-message-line-7-b884"
            }
            """));
        var logger = new RecordingLogger();
        var registration = CreateRegistration(
            transport,
            new FakeTimeProvider(),
            new FixedRetryJitter());

        CentralConfigLoadException exception = await Assert.ThrowsAsync<CentralConfigLoadException>(() =>
            registration.RegisterAsync(host.Builder, logger, CancellationToken.None));

        Assert.Equal(CentralConfigFailureKind.InvalidResponse, exception.FailureKind);
        string diagnosticText = string.Join(
            Environment.NewLine,
            logger.Entries.SelectMany(entry =>
                new[] { entry.Message, entry.OriginalFormat }
                    .Concat(entry.Fields.Select(field => $"{field.Key}={field.Value}"))));
        Assert.All(prohibited, marker =>
        {
            Assert.DoesNotContain(marker, diagnosticText, StringComparison.Ordinal);
            Assert.DoesNotContain(marker, exception.ToString(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Throwing_logger_cannot_change_success_or_terminal_failure()
    {
        using var successHost = EnabledHost();
        var successTransport = new FakeDistributionTransport();
        successTransport.EnqueueResponse(Ok(
            "{\"values\":{\"Demo:Required\":\"central\",\"Demo:Empty\":\"\"},\"missingKeys\":[\"Demo:Fallback\"]}"));
        var successRegistration = CreateRegistration(
            successTransport,
            new FakeTimeProvider(),
            new FixedRetryJitter());

        await successRegistration.RegisterAsync(successHost.Builder, new ThrowingLogger(false), CancellationToken.None);
        Assert.Equal("central", successHost.Builder.Configuration["Demo:Required"]);

        using var failureHost = EnabledHost();
        var failureTransport = new FakeDistributionTransport();
        failureTransport.EnqueueResponse(new DistributionHttpResponse(HttpStatusCode.Forbidden, ReadOnlyMemory<byte>.Empty));
        var failureRegistration = CreateRegistration(
            failureTransport,
            new FakeTimeProvider(),
            new FixedRetryJitter());

        CentralConfigLoadException failure = await Assert.ThrowsAsync<CentralConfigLoadException>(() =>
            failureRegistration.RegisterAsync(failureHost.Builder, new ThrowingLogger(true), CancellationToken.None));
        Assert.Equal(CentralConfigFailureKind.Unauthorized, failure.FailureKind);
    }

    private static CentralConfigRegistration CreateRegistration(
        IDistributionTransport transport,
        TimeProvider timeProvider,
        IRetryJitter jitter,
        IDistributionResponseParser? responseParser = null,
        ISnapshotSourcePreparer? snapshotSourcePreparer = null,
        ISnapshotCommitter? snapshotCommitter = null) =>
        new(
            transport,
            timeProvider,
            jitter,
            responseParser ?? DistributionResponseParser.Instance,
            snapshotSourcePreparer ?? SnapshotSourcePreparer.Instance,
            snapshotCommitter ?? SnapshotCommitter.Instance);

    private static TestHostBuilder EnabledHost() => new(
        """
        {
          "CentralConfig": {
            "Enabled": true,
            "BaseAddress": "https://central/gateway",
            "Environment": "UAT",
            "CentralRequiredKeys": [ "Demo:Required", "Demo:Empty" ],
            "FallbackAllowedKeys": [ "Demo:Fallback" ]
          },
          "Demo": {
            "Required": "base-required",
            "Empty": "base-empty",
            "Fallback": "base-fallback"
          }
        }
        """,
        """
        {
          "Demo": {
            "Required": "environment-required",
            "Fallback": "environment-fallback"
          }
        }
        """);

    private static DistributionHttpResponse Ok(string json) =>
        new(HttpStatusCode.OK, Encoding.UTF8.GetBytes(json));
}
