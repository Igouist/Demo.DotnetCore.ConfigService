using System.Net;
using System.Text;
using Demo.DotnetCore.ConfigService.Client.Tests.Infrastructure;
using Microsoft.Extensions.Time.Testing;

namespace Demo.DotnetCore.ConfigService.Client.Tests.Loading;

public sealed class RetryingDistributionResolverTests
{
    [Fact]
    public async Task Uses_fixed_delays_independent_jitter_and_stops_on_success()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeDistributionTransport();
        transport.EnqueueResponse(Response(HttpStatusCode.ServiceUnavailable));
        transport.EnqueueResponse(Response(HttpStatusCode.TooManyRequests));
        transport.EnqueueFailure(new HttpRequestException("must-not-escape"));
        transport.EnqueueResponse(ValidResponse());
        var logger = new RecordingLogger();
        var jitter = new FixedRetryJitter(
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(250));
        var resolver = CreateResolver(transport, time, jitter, logger);
        using var total = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
        long started = time.GetTimestamp();

        Task<ResolvedCentralConfig> load = resolver.ResolveAsync(
            EnabledBootstrap(),
            started,
            CancellationToken.None,
            total.Token);

        await transport.WaitForNextCallAsync();
        await logger.WaitForEntryCountAsync(1);
        time.Advance(TimeSpan.FromSeconds(1));
        await transport.WaitForNextCallAsync();
        await logger.WaitForEntryCountAsync(2);
        time.Advance(TimeSpan.FromSeconds(2.1));
        await transport.WaitForNextCallAsync();
        await logger.WaitForEntryCountAsync(3);
        time.Advance(TimeSpan.FromSeconds(4.25));
        await transport.WaitForNextCallAsync();
        ResolvedCentralConfig result = await load;

        Assert.Equal(4, result.AttemptCount);
        Assert.Equal("central", result.Values["Demo:Required"]);
        Assert.Equal(1, result.MissingFallbackKeyCount);
        Assert.Equal(4, transport.CallCount);
        Assert.Equal(3, jitter.CallCount);
        LogEntry[] warnings = logger.Entries
            .Where(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning)
            .ToArray();
        Assert.Collection(
            warnings,
            entry => AssertWarning(
                entry,
                1,
                CentralConfigFailureKind.HttpFailure,
                HttpStatusCode.ServiceUnavailable,
                1_000d),
            entry => AssertWarning(
                entry,
                2,
                CentralConfigFailureKind.HttpFailure,
                HttpStatusCode.TooManyRequests,
                2_100d),
            entry => AssertWarning(
                entry,
                3,
                CentralConfigFailureKind.TransportFailure,
                null,
                4_250d));
    }

    [Fact]
    public async Task Each_retry_warning_carries_the_central_problem_reference_of_its_own_attempt()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeDistributionTransport();
        transport.EnqueueResponse(new DistributionHttpResponse(
            HttpStatusCode.InternalServerError,
            ReadOnlyMemory<byte>.Empty,
            new CentralProblemReference("persistence_failure", "0HN1FIRST:00000001")));
        transport.EnqueueResponse(new DistributionHttpResponse(
            HttpStatusCode.InternalServerError,
            ReadOnlyMemory<byte>.Empty,
            new CentralProblemReference("internal_error", "0HN1SECOND:00000002")));
        transport.EnqueueResponse(Response(HttpStatusCode.ServiceUnavailable));
        transport.EnqueueResponse(ValidResponse());
        var logger = new RecordingLogger();
        var resolver = CreateResolver(
            transport,
            time,
            new FixedRetryJitter(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero),
            logger);
        using var total = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);

        Task<ResolvedCentralConfig> load = resolver.ResolveAsync(
            EnabledBootstrap(),
            time.GetTimestamp(),
            CancellationToken.None,
            total.Token);
        await transport.WaitForNextCallAsync();
        await logger.WaitForEntryCountAsync(1);
        time.Advance(TimeSpan.FromSeconds(1));
        await transport.WaitForNextCallAsync();
        await logger.WaitForEntryCountAsync(2);
        time.Advance(TimeSpan.FromSeconds(2));
        await transport.WaitForNextCallAsync();
        await logger.WaitForEntryCountAsync(3);
        time.Advance(TimeSpan.FromSeconds(4));
        await transport.WaitForNextCallAsync();
        ResolvedCentralConfig result = await load;

        Assert.Equal(4, result.AttemptCount);
        Assert.Collection(
            logger.Entries.Where(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning),
            entry => AssertWarning(
                entry,
                1,
                CentralConfigFailureKind.HttpFailure,
                HttpStatusCode.InternalServerError,
                1_000d,
                "persistence_failure",
                "0HN1FIRST:00000001"),
            entry => AssertWarning(
                entry,
                2,
                CentralConfigFailureKind.HttpFailure,
                HttpStatusCode.InternalServerError,
                2_000d,
                "internal_error",
                "0HN1SECOND:00000002"),
            entry => AssertWarning(
                entry,
                3,
                CentralConfigFailureKind.HttpFailure,
                HttpStatusCode.ServiceUnavailable,
                4_000d));
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task Every_accepted_retryable_status_schedules_the_next_attempt(HttpStatusCode statusCode)
    {
        var time = new FakeTimeProvider();
        var transport = new FakeDistributionTransport();
        transport.EnqueueResponse(Response(statusCode));
        transport.EnqueueResponse(ValidResponse());
        var logger = new RecordingLogger();
        var resolver = CreateResolver(
            transport,
            time,
            new FixedRetryJitter(TimeSpan.Zero),
            logger);
        using var total = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
        Task<ResolvedCentralConfig> load = resolver.ResolveAsync(
            EnabledBootstrap(),
            time.GetTimestamp(),
            CancellationToken.None,
            total.Token);
        await transport.WaitForNextCallAsync();

        await logger.WaitForEntryCountAsync(1);
        time.Advance(TimeSpan.FromSeconds(1));
        await transport.WaitForNextCallAsync();
        ResolvedCentralConfig result = await load;

        Assert.Equal(2, result.AttemptCount);
        Assert.Equal(2, transport.CallCount);
    }

    [Fact]
    public async Task Retryable_http_exhaustion_is_a_typed_http_failure()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeDistributionTransport();
        for (int index = 0; index < 4; index++)
        {
            transport.EnqueueResponse(Response(HttpStatusCode.ServiceUnavailable));
        }

        var logger = new RecordingLogger();
        var resolver = CreateResolver(
            transport,
            time,
            new FixedRetryJitter(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero),
            logger);
        using var total = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
        Task<ResolvedCentralConfig> load = resolver.ResolveAsync(
            EnabledBootstrap(),
            time.GetTimestamp(),
            CancellationToken.None,
            total.Token);
        await transport.WaitForNextCallAsync();
        await logger.WaitForEntryCountAsync(1);
        time.Advance(TimeSpan.FromSeconds(1));
        await transport.WaitForNextCallAsync();
        await logger.WaitForEntryCountAsync(2);
        time.Advance(TimeSpan.FromSeconds(2));
        await transport.WaitForNextCallAsync();
        await logger.WaitForEntryCountAsync(3);
        time.Advance(TimeSpan.FromSeconds(4));
        await transport.WaitForNextCallAsync();

        CentralConfigLoadException exception = await Assert.ThrowsAsync<CentralConfigLoadException>(() => load);

        Assert.Equal(CentralConfigFailureKind.HttpFailure, exception.FailureKind);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.HttpStatusCode);
        Assert.Equal(4, exception.AttemptCount);
    }

    [Fact]
    public async Task Transport_exhaustion_is_a_typed_transport_failure_without_inner_exception()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeDistributionTransport();
        for (int index = 0; index < 4; index++)
        {
            transport.EnqueueFailure(new HttpRequestException("must-not-escape"));
        }

        var logger = new RecordingLogger();
        var resolver = CreateResolver(
            transport,
            time,
            new FixedRetryJitter(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero),
            logger);
        using var total = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
        Task<ResolvedCentralConfig> load = resolver.ResolveAsync(
            EnabledBootstrap(),
            time.GetTimestamp(),
            CancellationToken.None,
            total.Token);
        await transport.WaitForNextCallAsync();
        await logger.WaitForEntryCountAsync(1);
        time.Advance(TimeSpan.FromSeconds(1));
        await transport.WaitForNextCallAsync();
        await logger.WaitForEntryCountAsync(2);
        time.Advance(TimeSpan.FromSeconds(2));
        await transport.WaitForNextCallAsync();
        await logger.WaitForEntryCountAsync(3);
        time.Advance(TimeSpan.FromSeconds(4));
        await transport.WaitForNextCallAsync();

        CentralConfigLoadException exception = await Assert.ThrowsAsync<CentralConfigLoadException>(() => load);

        Assert.Equal(CentralConfigFailureKind.TransportFailure, exception.FailureKind);
        Assert.Null(exception.HttpStatusCode);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("must-not-escape", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Null_transport_result_preserves_safe_transport_failure_classification()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeDistributionTransport();
        for (int index = 0; index < 3; index++)
        {
            transport.EnqueueFailure(new HttpRequestException("earlier"));
        }

        transport.EnqueueResponse(null!);
        var logger = new RecordingLogger();
        var resolver = CreateResolver(
            transport,
            time,
            new FixedRetryJitter(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero),
            logger);
        using var total = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
        Task<ResolvedCentralConfig> load = resolver.ResolveAsync(
            EnabledBootstrap(),
            time.GetTimestamp(),
            CancellationToken.None,
            total.Token);
        await transport.WaitForNextCallAsync();
        await logger.WaitForEntryCountAsync(1);
        time.Advance(TimeSpan.FromSeconds(1));
        await transport.WaitForNextCallAsync();
        await logger.WaitForEntryCountAsync(2);
        time.Advance(TimeSpan.FromSeconds(2));
        await transport.WaitForNextCallAsync();
        await logger.WaitForEntryCountAsync(3);
        time.Advance(TimeSpan.FromSeconds(4));
        await transport.WaitForNextCallAsync();

        CentralConfigLoadException exception = await Assert.ThrowsAsync<CentralConfigLoadException>(() => load);

        Assert.Equal(CentralConfigFailureKind.TransportFailure, exception.FailureKind);
        Assert.Equal(4, exception.AttemptCount);
        Assert.Null(exception.HttpStatusCode);
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, CentralConfigFailureKind.HttpFailure)]
    [InlineData(HttpStatusCode.Unauthorized, CentralConfigFailureKind.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, CentralConfigFailureKind.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound, CentralConfigFailureKind.HttpFailure)]
    [InlineData(HttpStatusCode.Created, CentralConfigFailureKind.HttpFailure)]
    public async Task Non_retryable_status_fails_after_one_attempt(
        HttpStatusCode statusCode,
        CentralConfigFailureKind expectedKind)
    {
        var time = new FakeTimeProvider();
        var transport = new FakeDistributionTransport();
        transport.EnqueueResponse(Response(statusCode));
        var resolver = CreateResolver(
            transport,
            time,
            new FixedRetryJitter(),
            new RecordingLogger());
        using var total = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);

        CentralConfigLoadException exception = await Assert.ThrowsAsync<CentralConfigLoadException>(() =>
            resolver.ResolveAsync(
                EnabledBootstrap(),
                time.GetTimestamp(),
                CancellationToken.None,
                total.Token));

        Assert.Equal(expectedKind, exception.FailureKind);
        Assert.Equal(statusCode, exception.HttpStatusCode);
        Assert.Equal(1, exception.AttemptCount);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task Invalid_200_and_missing_required_do_not_retry()
    {
        var time = new FakeTimeProvider();
        var invalidTransport = new FakeDistributionTransport();
        invalidTransport.EnqueueResponse(Response(HttpStatusCode.OK, "{}"));
        using var invalidTotal = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);

        CentralConfigLoadException invalid = await Assert.ThrowsAsync<CentralConfigLoadException>(() =>
            CreateResolver(invalidTransport, time, new FixedRetryJitter(), new RecordingLogger())
                .ResolveAsync(EnabledBootstrap(), time.GetTimestamp(), CancellationToken.None, invalidTotal.Token));

        Assert.Equal(CentralConfigFailureKind.InvalidResponse, invalid.FailureKind);
        Assert.Equal(HttpStatusCode.OK, invalid.HttpStatusCode);
        Assert.Equal(1, invalidTransport.CallCount);

        var missingTransport = new FakeDistributionTransport();
        missingTransport.EnqueueResponse(Response(
            HttpStatusCode.OK,
            "{\"values\":{},\"missingKeys\":[\"demo:second\",\"Demo:Fallback\",\"DEMO:FIRST\"]}"));
        using var missingTotal = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
        EnabledCentralConfigBootstrap missingBootstrap = EnabledBootstrap(
            ["Demo:First", "DEMO:Second"],
            ["Demo:Fallback"]);

        CentralConfigLoadException missing = await Assert.ThrowsAsync<CentralConfigLoadException>(() =>
            CreateResolver(missingTransport, time, new FixedRetryJitter(), new RecordingLogger())
                .ResolveAsync(missingBootstrap, time.GetTimestamp(), CancellationToken.None, missingTotal.Token));

        Assert.Equal(CentralConfigFailureKind.MissingRequiredKeys, missing.FailureKind);
        Assert.Equal(["Demo:First", "DEMO:Second"], missing.MissingRequiredKeys);
        Assert.Equal(1, missingTransport.CallCount);
    }

    [Fact]
    public async Task Total_timeout_before_http_is_typed_with_zero_attempts_and_no_transport_call()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeDistributionTransport();
        using var total = new CancellationTokenSource();
        total.Cancel();

        CentralConfigLoadException exception = await Assert.ThrowsAsync<CentralConfigLoadException>(() =>
            CreateResolver(transport, time, new FixedRetryJitter(), new RecordingLogger())
                .ResolveAsync(
                    EnabledBootstrap(),
                    time.GetTimestamp(),
                    CancellationToken.None,
                    total.Token));

        Assert.Equal(CentralConfigFailureKind.TotalTimeout, exception.FailureKind);
        Assert.Equal(0, exception.AttemptCount);
        Assert.Equal(TimeSpan.Zero, exception.Elapsed);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task Cancellation_before_retry_schedule_consumes_no_jitter_and_emits_no_warning()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeDistributionTransport();
        TaskCompletionSource<DistributionHttpResponse> completed = transport.EnqueueNonCooperativeResponse();
        var logger = new RecordingLogger();
        var jitter = new FixedRetryJitter(TimeSpan.Zero);
        var resolver = CreateResolver(transport, time, jitter, logger);
        using var caller = new CancellationTokenSource();
        using var total = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
        Task<ResolvedCentralConfig> load = resolver.ResolveAsync(
            EnabledBootstrap(),
            time.GetTimestamp(),
            caller.Token,
            total.Token);
        await transport.WaitForNextCallAsync();

        completed.SetResult(Response(HttpStatusCode.ServiceUnavailable));
        caller.Cancel();
        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);

        Assert.Equal(caller.Token, exception.CancellationToken);
        Assert.Equal(0, jitter.CallCount);
        Assert.Empty(logger.Entries);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public void Retry_schedule_arbiter_latches_cancellation_before_scheduling()
    {
        using var caller = new CancellationTokenSource();
        using var total = new CancellationTokenSource();
        using var arbiter = new RetryScheduleArbiter(caller.Token, total.Token);
        caller.Cancel();

        RetryScheduleDecision decision = arbiter.TrySchedule();

        Assert.False(decision.IsScheduled);
        Assert.True(decision.Cancellation.CallerRequested);
        Assert.False(decision.Cancellation.TotalTimeoutRequested);
        Assert.False(decision.Cancellation.AttemptTimeoutRequested);
        Assert.Equal(OwnedCancellationKind.Caller, decision.Cancellation.HighestPriority);
    }

    [Fact]
    public async Task Cancellation_after_retry_schedule_consumes_one_jitter_and_emits_one_warning()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeDistributionTransport();
        transport.EnqueueResponse(Response(HttpStatusCode.ServiceUnavailable));
        var logger = new RecordingLogger();
        using var caller = new CancellationTokenSource();
        var jitter = new FixedRetryJitter(TimeSpan.Zero)
        {
            OnNext = caller.Cancel
        };
        var resolver = CreateResolver(transport, time, jitter, logger);
        using var total = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            resolver.ResolveAsync(
                EnabledBootstrap(),
                time.GetTimestamp(),
                caller.Token,
                total.Token));

        Assert.Equal(caller.Token, exception.CancellationToken);
        Assert.Equal(1, jitter.CallCount);
        LogEntry warning = Assert.Single(logger.Entries);
        AssertWarning(
            warning,
            1,
            CentralConfigFailureKind.HttpFailure,
            HttpStatusCode.ServiceUnavailable,
            1_000d);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task Caller_cancellation_during_retry_delay_keeps_original_token_and_starts_no_next_attempt()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeDistributionTransport();
        transport.EnqueueResponse(Response(HttpStatusCode.ServiceUnavailable));
        var logger = new RecordingLogger();
        var resolver = CreateResolver(
            transport,
            time,
            new FixedRetryJitter(TimeSpan.Zero),
            logger);
        using var caller = new CancellationTokenSource();
        using var total = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
        Task<ResolvedCentralConfig> load = resolver.ResolveAsync(
            EnabledBootstrap(),
            time.GetTimestamp(),
            caller.Token,
            total.Token);
        await transport.WaitForNextCallAsync();
        await logger.WaitForEntryCountAsync(1);

        caller.Cancel();
        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);

        Assert.Equal(caller.Token, exception.CancellationToken);
        Assert.Equal(1, transport.CallCount);
        Assert.DoesNotContain(logger.Entries, entry =>
            entry.Level == Microsoft.Extensions.Logging.LogLevel.Error);
    }

    [Fact]
    public async Task Thirty_second_total_budget_includes_retry_delay()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeDistributionTransport();
        transport.EnqueueResponse(Response(HttpStatusCode.ServiceUnavailable));
        var logger = new RecordingLogger();
        var resolver = CreateResolver(
            transport,
            time,
            new FixedRetryJitter(TimeSpan.Zero),
            logger);
        long started = time.GetTimestamp();
        using var total = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
        time.Advance(TimeSpan.FromSeconds(29));
        Task<ResolvedCentralConfig> load = resolver.ResolveAsync(
            EnabledBootstrap(),
            started,
            CancellationToken.None,
            total.Token);
        await transport.WaitForNextCallAsync();
        await logger.WaitForEntryCountAsync(1);

        time.Advance(TimeSpan.FromSeconds(1));
        CentralConfigLoadException exception = await Assert.ThrowsAsync<CentralConfigLoadException>(() => load);

        Assert.Equal(CentralConfigFailureKind.TotalTimeout, exception.FailureKind);
        Assert.Equal(1, exception.AttemptCount);
        Assert.Equal(TimeSpan.FromSeconds(30), exception.Elapsed);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task Total_timeout_wins_when_attempt_and_total_tokens_are_both_requested()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeDistributionTransport();
        transport.EnqueueBlockingResponse();
        var resolver = CreateResolver(
            transport,
            time,
            new FixedRetryJitter(),
            new RecordingLogger());
        using var total = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
        Task<ResolvedCentralConfig> load = resolver.ResolveAsync(
            EnabledBootstrap(),
            time.GetTimestamp(),
            CancellationToken.None,
            total.Token);
        await transport.WaitForNextCallAsync();

        time.Advance(TimeSpan.FromSeconds(5));
        CentralConfigLoadException exception = await Assert.ThrowsAsync<CentralConfigLoadException>(() => load);

        Assert.Equal(CentralConfigFailureKind.TotalTimeout, exception.FailureKind);
        Assert.Equal(1, exception.AttemptCount);
        Assert.Equal(TimeSpan.FromSeconds(5), exception.Elapsed);
    }

    [Fact]
    public async Task Caller_wins_when_all_owned_cancellations_are_requested_at_the_same_instant()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeDistributionTransport();
        transport.EnqueueBlockingResponse();
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
        using var total = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
        Task<ResolvedCentralConfig> load = CreateResolver(
                transport,
                time,
                new FixedRetryJitter(),
                new RecordingLogger())
            .ResolveAsync(
                EnabledBootstrap(),
                time.GetTimestamp(),
                caller.Token,
                total.Token);
        await transport.WaitForNextCallAsync();

        time.Advance(TimeSpan.FromSeconds(5));
        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);

        Assert.True(caller.IsCancellationRequested);
        Assert.True(total.IsCancellationRequested);
        Assert.Equal(caller.Token, exception.CancellationToken);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task Attempt_arbiter_latches_all_requested_tokens_with_caller_priority()
    {
        using var caller = new CancellationTokenSource();
        using var total = new CancellationTokenSource();
        using var attempt = new CancellationTokenSource();
        caller.Cancel();
        total.Cancel();
        attempt.Cancel();
        using var arbiter = new AttemptArbiter(caller.Token, total.Token, attempt.Token);
        bool transportStarted = false;

        AttemptArbitration arbitration = await arbiter.Start(_ =>
        {
            transportStarted = true;
            return Task.FromResult(ValidResponse());
        });

        Assert.False(arbitration.TransportCompleted);
        Assert.False(arbitration.TransportStarted);
        Assert.False(transportStarted);
        Assert.True(arbitration.Cancellation.CallerRequested);
        Assert.True(arbitration.Cancellation.TotalTimeoutRequested);
        Assert.True(arbitration.Cancellation.AttemptTimeoutRequested);
        Assert.Equal(OwnedCancellationKind.Caller, arbitration.Cancellation.HighestPriority);
    }

    [Theory]
    [InlineData(nameof(OwnedCancellationKind.Caller))]
    [InlineData(nameof(OwnedCancellationKind.TotalTimeout))]
    [InlineData(nameof(OwnedCancellationKind.AttemptTimeout))]
    public async Task Synchronously_blocking_transport_start_cannot_delay_owned_cancellation(
        string cancellationName)
    {
        OwnedCancellationKind cancellationKind =
            Enum.Parse<OwnedCancellationKind>(cancellationName);
        using var startGate = new BlockingCallbackGate();
        using var caller = new CancellationTokenSource();
        using var total = new CancellationTokenSource();
        using var attempt = new CancellationTokenSource();
        using var arbiter = new AttemptArbiter(
            caller.Token,
            total.Token,
            attempt.Token);
        Task<AttemptArbitration> arbitrationTask = Task.Run(() => arbiter.Start(_ =>
        {
            startGate.Block();
            return Task.FromResult(ValidResponse());
        }));
        await startGate.WaitUntilEnteredAsync();

        Task cancellationTask = Task.Run(() => CancellationSource(cancellationKind).Cancel());
        AttemptArbitration arbitration = default;

        try
        {
            arbitration = await arbitrationTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            startGate.Release();
            await cancellationTask.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(arbitration.TransportStarted);
        Assert.False(arbitration.TransportCompleted);
        Assert.Equal(cancellationKind, arbitration.Cancellation.HighestPriority);

        CancellationTokenSource CancellationSource(OwnedCancellationKind kind) => kind switch
        {
            OwnedCancellationKind.Caller => caller,
            OwnedCancellationKind.TotalTimeout => total,
            OwnedCancellationKind.AttemptTimeout => attempt,
            _ => throw new InvalidOperationException("An owned cancellation kind is required.")
        };
    }

    [Theory]
    [InlineData(nameof(OwnedCancellationKind.Caller))]
    [InlineData(nameof(OwnedCancellationKind.TotalTimeout))]
    [InlineData(nameof(OwnedCancellationKind.AttemptTimeout))]
    public async Task Resolver_classifies_owned_cancellation_while_transport_start_is_synchronously_blocked(
        string cancellationName)
    {
        OwnedCancellationKind cancellationKind =
            Enum.Parse<OwnedCancellationKind>(cancellationName);
        var time = new FakeTimeProvider();
        var transport = new FakeDistributionTransport();
        if (cancellationKind == OwnedCancellationKind.AttemptTimeout)
        {
            for (int index = 0; index < 3; index++)
            {
                transport.EnqueueFailure(new HttpRequestException("earlier"));
            }
        }

        using var startGate = new BlockingCallbackGate();
        Task transportFinished = transport.EnqueueSynchronouslyBlockingResponse(
            startGate,
            ValidResponse());
        using var caller = new CancellationTokenSource();
        using var total = new CancellationTokenSource();
        var logger = new RecordingLogger();
        Task<ResolvedCentralConfig> load = CreateResolver(
                transport,
                time,
                new FixedRetryJitter(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero),
                logger)
            .ResolveAsync(
                EnabledBootstrap(),
                time.GetTimestamp(),
                caller.Token,
                total.Token);
        if (cancellationKind == OwnedCancellationKind.AttemptTimeout)
        {
            await transport.WaitForNextCallAsync();
            await logger.WaitForEntryCountAsync(1);
            time.Advance(TimeSpan.FromSeconds(1));
            await transport.WaitForNextCallAsync();
            await logger.WaitForEntryCountAsync(2);
            time.Advance(TimeSpan.FromSeconds(2));
            await transport.WaitForNextCallAsync();
            await logger.WaitForEntryCountAsync(3);
            time.Advance(TimeSpan.FromSeconds(4));
        }

        await transport.WaitForNextCallAsync();
        await startGate.WaitUntilEnteredAsync();
        Task cancellationTask = Task.Run(() =>
        {
            if (cancellationKind == OwnedCancellationKind.Caller)
            {
                caller.Cancel();
            }
            else if (cancellationKind == OwnedCancellationKind.TotalTimeout)
            {
                total.Cancel();
            }
            else
            {
                time.Advance(TimeSpan.FromSeconds(5));
            }
        });
        Exception? observed = null;

        try
        {
            observed = await Record.ExceptionAsync(async () =>
            {
                _ = await load.WaitAsync(TimeSpan.FromSeconds(2));
            });
        }
        finally
        {
            startGate.Release();
            await cancellationTask.WaitAsync(TimeSpan.FromSeconds(5));
            await transportFinished.WaitAsync(TimeSpan.FromSeconds(5));
        }

        if (cancellationKind == OwnedCancellationKind.Caller)
        {
            OperationCanceledException exception =
                Assert.IsAssignableFrom<OperationCanceledException>(observed);
            Assert.Equal(caller.Token, exception.CancellationToken);
        }
        else
        {
            CentralConfigLoadException exception =
                Assert.IsType<CentralConfigLoadException>(observed);
            Assert.Equal(
                cancellationKind == OwnedCancellationKind.TotalTimeout
                    ? CentralConfigFailureKind.TotalTimeout
                    : CentralConfigFailureKind.AttemptTimeout,
                exception.FailureKind);
            Assert.Equal(
                cancellationKind == OwnedCancellationKind.TotalTimeout ? 1 : 4,
                exception.AttemptCount);
        }

        Assert.Equal(
            cancellationKind == OwnedCancellationKind.AttemptTimeout ? 4 : 1,
            transport.CallCount);
    }

    [Theory]
    [InlineData(nameof(OwnedCancellationKind.Caller))]
    [InlineData(nameof(OwnedCancellationKind.TotalTimeout))]
    [InlineData(nameof(OwnedCancellationKind.AttemptTimeout))]
    public async Task Cancellation_claimed_before_transport_completion_keeps_its_outcome(
        string cancellationName)
    {
        OwnedCancellationKind cancellationKind =
            Enum.Parse<OwnedCancellationKind>(cancellationName);
        var completionObservation = new ControlledAttemptCompletionObservation();
        using var cancellationGate = new BlockingCallbackGate();
        using var caller = new CancellationTokenSource();
        using var total = new CancellationTokenSource();
        using var attempt = new CancellationTokenSource();
        using var arbiter = new AttemptArbiter(
            caller.Token,
            total.Token,
            attempt.Token,
            completionObservation.Capture,
            cancellationGate.Block);
        var transport = new TaskCompletionSource<DistributionHttpResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<AttemptArbitration> arbitrationTask = arbiter.Start(_ => transport.Task);
        Task cancellationTask = Task.Run(() => CancellationSource(cancellationKind).Cancel());
        await cancellationGate.WaitUntilEnteredAsync();

        transport.SetResult(ValidResponse());
        completionObservation.ObserveNow();
        cancellationGate.Release();
        await cancellationTask.WaitAsync(TimeSpan.FromSeconds(5));
        AttemptArbitration arbitration = await arbitrationTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(arbitration.TransportStarted);
        Assert.False(arbitration.TransportCompleted);
        Assert.Equal(cancellationKind, arbitration.Cancellation.HighestPriority);

        CancellationTokenSource CancellationSource(OwnedCancellationKind kind) => kind switch
        {
            OwnedCancellationKind.Caller => caller,
            OwnedCancellationKind.TotalTimeout => total,
            OwnedCancellationKind.AttemptTimeout => attempt,
            _ => throw new InvalidOperationException("An owned cancellation kind is required.")
        };
    }

    [Fact]
    public async Task Claimed_cancellation_accumulates_simultaneous_sources_in_priority_order()
    {
        var completionObservation = new ControlledAttemptCompletionObservation();
        using var cancellationGate = new CountingBlockingCallbackGate(expectedCount: 3);
        using var caller = new CancellationTokenSource();
        using var total = new CancellationTokenSource();
        using var attempt = new CancellationTokenSource();
        using var arbiter = new AttemptArbiter(
            caller.Token,
            total.Token,
            attempt.Token,
            completionObservation.Capture,
            cancellationGate.Block);
        var transport = new TaskCompletionSource<DistributionHttpResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<AttemptArbitration> arbitrationTask = arbiter.Start(_ => transport.Task);
        Task[] cancellationTasks =
        [
            Task.Run(attempt.Cancel),
            Task.Run(total.Cancel),
            Task.Run(caller.Cancel)
        ];
        await cancellationGate.WaitUntilAllEnteredAsync();

        transport.SetResult(ValidResponse());
        completionObservation.ObserveNow();
        cancellationGate.Release();
        await Task.WhenAll(cancellationTasks).WaitAsync(TimeSpan.FromSeconds(5));
        AttemptArbitration arbitration = await arbitrationTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(arbitration.TransportCompleted);
        Assert.True(arbitration.Cancellation.CallerRequested);
        Assert.True(arbitration.Cancellation.TotalTimeoutRequested);
        Assert.True(arbitration.Cancellation.AttemptTimeoutRequested);
        Assert.Equal(OwnedCancellationKind.Caller, arbitration.Cancellation.HighestPriority);
    }

    [Theory]
    [InlineData(nameof(OwnedCancellationKind.Caller))]
    [InlineData(nameof(OwnedCancellationKind.TotalTimeout))]
    public async Task Cancellation_after_transport_completion_before_validation_remains_authoritative(
        string cancellationName)
    {
        OwnedCancellationKind cancellationKind =
            Enum.Parse<OwnedCancellationKind>(cancellationName);
        var time = new FakeTimeProvider();
        var transport = new FakeDistributionTransport();
        TaskCompletionSource<DistributionHttpResponse> response =
            transport.EnqueueNonCooperativeResponse();
        using var caller = new CancellationTokenSource();
        using var total = new CancellationTokenSource();
        using var context = new ControlledSynchronizationContext();
        SynchronizationContext? prior = SynchronizationContext.Current;
        Task<ResolvedCentralConfig> load;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            load = CreateResolver(
                    transport,
                    time,
                    new FixedRetryJitter(),
                    new RecordingLogger())
                .ResolveAsync(
                    EnabledBootstrap(),
                    time.GetTimestamp(),
                    caller.Token,
                    total.Token);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prior);
        }

        await transport.WaitForNextCallAsync();
        response.SetResult(ValidResponse());
        Action continueAfterTransport = await context.TakeNextAsync();
        CancellationSource().Cancel();
        continueAfterTransport();

        Exception? observed = await Record.ExceptionAsync(() => load);
        if (cancellationKind == OwnedCancellationKind.Caller)
        {
            OperationCanceledException exception =
                Assert.IsAssignableFrom<OperationCanceledException>(observed);
            Assert.Equal(caller.Token, exception.CancellationToken);
        }
        else
        {
            CentralConfigLoadException exception =
                Assert.IsType<CentralConfigLoadException>(observed);
            Assert.Equal(CentralConfigFailureKind.TotalTimeout, exception.FailureKind);
            Assert.Equal(1, exception.AttemptCount);
        }

        CancellationTokenSource CancellationSource() =>
            cancellationKind == OwnedCancellationKind.Caller ? caller : total;
    }

    [Theory]
    [InlineData(nameof(OwnedCancellationKind.Caller))]
    [InlineData(nameof(OwnedCancellationKind.TotalTimeout))]
    public async Task Blocking_response_validation_cannot_delay_caller_or_total_outcome(
        string cancellationName)
    {
        OwnedCancellationKind cancellationKind =
            Enum.Parse<OwnedCancellationKind>(cancellationName);
        var time = new FakeTimeProvider();
        var transport = new FakeDistributionTransport();
        transport.EnqueueResponse(ValidResponse());
        using var parser = new BlockingDistributionResponseParser();
        using var caller = new CancellationTokenSource();
        using var total = new CancellationTokenSource();
        Task<ResolvedCentralConfig> load = CreateResolver(
                transport,
                time,
                new FixedRetryJitter(),
                new RecordingLogger(),
                parser)
            .ResolveAsync(
                EnabledBootstrap(),
                time.GetTimestamp(),
                caller.Token,
                total.Token);
        await parser.WaitUntilEnteredAsync();
        CancellationSource().Cancel();
        Exception? observed = null;

        try
        {
            observed = await Record.ExceptionAsync(async () =>
            {
                _ = await load.WaitAsync(TimeSpan.FromSeconds(2));
            });
        }
        finally
        {
            parser.Release();
            await parser.WaitUntilFinishedAsync();
        }

        if (cancellationKind == OwnedCancellationKind.Caller)
        {
            OperationCanceledException exception =
                Assert.IsAssignableFrom<OperationCanceledException>(observed);
            Assert.Equal(caller.Token, exception.CancellationToken);
        }
        else
        {
            CentralConfigLoadException exception =
                Assert.IsType<CentralConfigLoadException>(observed);
            Assert.Equal(CentralConfigFailureKind.TotalTimeout, exception.FailureKind);
            Assert.Equal(1, exception.AttemptCount);
        }

        CancellationTokenSource CancellationSource() =>
            cancellationKind == OwnedCancellationKind.Caller ? caller : total;
    }

    [Fact]
    public async Task Late_validation_fault_after_cancellation_does_not_change_the_latched_outcome()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeDistributionTransport();
        transport.EnqueueResponse(ValidResponse());
        using var parser = new BlockingDistributionResponseParser(
            new InvalidOperationException("late-validation-sensitive-marker"));
        using var caller = new CancellationTokenSource();
        using var total = new CancellationTokenSource();
        Task<ResolvedCentralConfig> load = CreateResolver(
                transport,
                time,
                new FixedRetryJitter(),
                new RecordingLogger(),
                parser)
            .ResolveAsync(
                EnabledBootstrap(),
                time.GetTimestamp(),
                caller.Token,
                total.Token);
        await parser.WaitUntilEnteredAsync();

        caller.Cancel();
        Exception? observed = null;
        try
        {
            observed = await Record.ExceptionAsync(async () =>
            {
                _ = await load.WaitAsync(TimeSpan.FromSeconds(2));
            });
        }
        finally
        {
            parser.Release();
            await parser.WaitUntilFinishedAsync();
        }

        OperationCanceledException exception =
            Assert.IsAssignableFrom<OperationCanceledException>(observed);
        Assert.Equal(caller.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task Caller_cancellation_wins_and_keeps_the_original_token()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeDistributionTransport();
        transport.EnqueueBlockingResponse();
        var logger = new RecordingLogger();
        var resolver = CreateResolver(transport, time, new FixedRetryJitter(), logger);
        using var caller = new CancellationTokenSource();
        using var total = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
        Task<ResolvedCentralConfig> load = resolver.ResolveAsync(
            EnabledBootstrap(),
            time.GetTimestamp(),
            caller.Token,
            total.Token);
        await transport.WaitForNextCallAsync();

        caller.Cancel();
        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);

        Assert.Equal(caller.Token, exception.CancellationToken);
        Assert.Equal(1, transport.CallCount);
        Assert.DoesNotContain(logger.Entries, entry =>
            entry.Level == Microsoft.Extensions.Logging.LogLevel.Error);
    }

    [Fact]
    public async Task Terminal_attempt_timeout_is_distinct_from_total_timeout()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeDistributionTransport();
        for (int index = 0; index < 4; index++)
        {
            transport.EnqueueBlockingResponse();
        }

        var logger = new RecordingLogger();
        var resolver = CreateResolver(
            transport,
            time,
            new FixedRetryJitter(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero),
            logger);
        using var total = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
        Task<ResolvedCentralConfig> load = resolver.ResolveAsync(
            EnabledBootstrap(),
            time.GetTimestamp(),
            CancellationToken.None,
            total.Token);

        await transport.WaitForNextCallAsync();
        time.Advance(TimeSpan.FromSeconds(5));
        await logger.WaitForEntryCountAsync(1);
        time.Advance(TimeSpan.FromSeconds(1));
        await transport.WaitForNextCallAsync();
        time.Advance(TimeSpan.FromSeconds(5));
        await logger.WaitForEntryCountAsync(2);
        time.Advance(TimeSpan.FromSeconds(2));
        await transport.WaitForNextCallAsync();
        time.Advance(TimeSpan.FromSeconds(5));
        await logger.WaitForEntryCountAsync(3);
        time.Advance(TimeSpan.FromSeconds(4));
        await transport.WaitForNextCallAsync();
        time.Advance(TimeSpan.FromSeconds(5));

        CentralConfigLoadException exception = await Assert.ThrowsAsync<CentralConfigLoadException>(() => load);

        Assert.Equal(CentralConfigFailureKind.AttemptTimeout, exception.FailureKind);
        Assert.Equal(4, exception.AttemptCount);
        Assert.Equal(TimeSpan.FromSeconds(27), exception.Elapsed);
    }

    [Fact]
    public async Task Unexpected_downstream_cancellation_is_a_retryable_transport_failure()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeDistributionTransport();
        transport.EnqueueFailure(new OperationCanceledException());
        transport.EnqueueResponse(ValidResponse());
        var logger = new RecordingLogger();
        var resolver = CreateResolver(
            transport,
            time,
            new FixedRetryJitter(TimeSpan.Zero),
            logger);
        using var total = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
        Task<ResolvedCentralConfig> load = resolver.ResolveAsync(
            EnabledBootstrap(),
            time.GetTimestamp(),
            CancellationToken.None,
            total.Token);
        await transport.WaitForNextCallAsync();

        await logger.WaitForEntryCountAsync(1);
        time.Advance(TimeSpan.FromSeconds(1));
        await transport.WaitForNextCallAsync();
        ResolvedCentralConfig result = await load;

        Assert.Equal(2, result.AttemptCount);
    }

    [Fact]
    public async Task Unexpected_downstream_cancellation_exhaustion_is_a_safe_transport_failure()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeDistributionTransport();
        for (int index = 0; index < 4; index++)
        {
            transport.EnqueueFailure(new OperationCanceledException("must-not-escape"));
        }

        var logger = new RecordingLogger();
        var resolver = CreateResolver(
            transport,
            time,
            new FixedRetryJitter(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero),
            logger);
        using var total = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
        Task<ResolvedCentralConfig> load = resolver.ResolveAsync(
            EnabledBootstrap(),
            time.GetTimestamp(),
            CancellationToken.None,
            total.Token);
        await transport.WaitForNextCallAsync();
        await logger.WaitForEntryCountAsync(1);
        time.Advance(TimeSpan.FromSeconds(1));
        await transport.WaitForNextCallAsync();
        await logger.WaitForEntryCountAsync(2);
        time.Advance(TimeSpan.FromSeconds(2));
        await transport.WaitForNextCallAsync();
        await logger.WaitForEntryCountAsync(3);
        time.Advance(TimeSpan.FromSeconds(4));
        await transport.WaitForNextCallAsync();

        CentralConfigLoadException exception = await Assert.ThrowsAsync<CentralConfigLoadException>(() => load);

        Assert.Equal(CentralConfigFailureKind.TransportFailure, exception.FailureKind);
        Assert.Equal(4, exception.AttemptCount);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("must-not-escape", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Completed_success_is_not_retroactively_relabelled_by_later_caller_cancellation()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeDistributionTransport();
        TaskCompletionSource<DistributionHttpResponse> completed = transport.EnqueueNonCooperativeResponse();
        using var caller = new CancellationTokenSource();
        using var total = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
        Task<ResolvedCentralConfig> load = CreateResolver(
                transport,
                time,
                new FixedRetryJitter(),
                new RecordingLogger())
            .ResolveAsync(
                EnabledBootstrap(),
                time.GetTimestamp(),
                caller.Token,
                total.Token);
        await transport.WaitForNextCallAsync();

        completed.SetResult(ValidResponse());
        ResolvedCentralConfig result = await load;
        caller.Cancel();

        Assert.Equal(1, result.AttemptCount);
        Assert.Equal("central", result.Values["Demo:Required"]);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task Completed_transport_wins_when_observed_before_later_cancellation()
    {
        var completionObservation = new ControlledAttemptCompletionObservation();
        using var caller = new CancellationTokenSource();
        using var total = new CancellationTokenSource();
        using var attempt = new CancellationTokenSource();
        using var arbiter = new AttemptArbiter(
            caller.Token,
            total.Token,
            attempt.Token,
            completionObservation.Capture,
            static () => { });
        var completed = new TaskCompletionSource<DistributionHttpResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<AttemptArbitration> arbitrationTask = arbiter.Start(_ => completed.Task);
        completed.SetResult(ValidResponse());
        await completionObservation.WaitForTransportCompletionAsync();
        completionObservation.ObserveNow();
        AttemptArbitration arbitration = await arbitrationTask.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();

        Assert.True(arbitration.TransportStarted);
        Assert.True(arbitration.TransportCompleted);
        Assert.True(arbitration.CompletedTransportTask!.IsCompletedSuccessfully);
        Assert.Equal(OwnedCancellationKind.None, arbitration.Cancellation.HighestPriority);
    }

    [Fact]
    public async Task Completed_terminal_failure_is_not_retroactively_relabelled_by_later_caller_cancellation()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeDistributionTransport();
        for (int index = 0; index < 3; index++)
        {
            transport.EnqueueFailure(new HttpRequestException("earlier"));
        }

        TaskCompletionSource<DistributionHttpResponse> completed = transport.EnqueueNonCooperativeResponse();
        var logger = new RecordingLogger();
        var resolver = CreateResolver(
            transport,
            time,
            new FixedRetryJitter(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero),
            logger);
        using var caller = new CancellationTokenSource();
        using var total = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
        Task<ResolvedCentralConfig> load = resolver.ResolveAsync(
            EnabledBootstrap(),
            time.GetTimestamp(),
            caller.Token,
            total.Token);
        await transport.WaitForNextCallAsync();
        await logger.WaitForEntryCountAsync(1);
        time.Advance(TimeSpan.FromSeconds(1));
        await transport.WaitForNextCallAsync();
        await logger.WaitForEntryCountAsync(2);
        time.Advance(TimeSpan.FromSeconds(2));
        await transport.WaitForNextCallAsync();
        await logger.WaitForEntryCountAsync(3);
        time.Advance(TimeSpan.FromSeconds(4));
        await transport.WaitForNextCallAsync();

        completed.SetException(new HttpRequestException("late-must-not-escape"));
        CentralConfigLoadException exception = await Assert.ThrowsAsync<CentralConfigLoadException>(() => load);
        caller.Cancel();

        Assert.Equal(CentralConfigFailureKind.TransportFailure, exception.FailureKind);
        Assert.Equal(4, exception.AttemptCount);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("late-must-not-escape", exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(4, transport.CallCount);
    }

    [Fact]
    public async Task Non_cooperative_success_after_attempt_timeout_is_not_accepted()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeDistributionTransport();
        TaskCompletionSource<DistributionHttpResponse> late = transport.EnqueueNonCooperativeResponse();
        transport.EnqueueResponse(ValidResponse());
        var logger = new RecordingLogger();
        var resolver = CreateResolver(
            transport,
            time,
            new FixedRetryJitter(TimeSpan.Zero),
            logger);
        using var total = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
        Task<ResolvedCentralConfig> load = resolver.ResolveAsync(
            EnabledBootstrap(),
            time.GetTimestamp(),
            CancellationToken.None,
            total.Token);
        await transport.WaitForNextCallAsync();

        time.Advance(TimeSpan.FromSeconds(5));
        late.SetResult(ValidResponse());
        await logger.WaitForEntryCountAsync(1);
        time.Advance(TimeSpan.FromSeconds(1));
        await transport.WaitForNextCallAsync();
        ResolvedCentralConfig result = await load;

        Assert.Equal(2, result.AttemptCount);
        Assert.Equal(2, transport.CallCount);
        Assert.Equal(CentralConfigFailureKind.AttemptTimeout, logger.Entries[0].Fields
            .Single(field => field.Key == "FailureKind").Value);
    }

    [Fact]
    public async Task Unresolved_non_cooperative_transport_cannot_defeat_attempt_timeout()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeDistributionTransport();
        _ = transport.EnqueueNonCooperativeResponse();
        transport.EnqueueResponse(ValidResponse());
        var logger = new RecordingLogger();
        var resolver = CreateResolver(
            transport,
            time,
            new FixedRetryJitter(TimeSpan.Zero),
            logger);
        using var total = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
        Task<ResolvedCentralConfig> load = resolver.ResolveAsync(
            EnabledBootstrap(),
            time.GetTimestamp(),
            CancellationToken.None,
            total.Token);
        await transport.WaitForNextCallAsync();

        time.Advance(TimeSpan.FromSeconds(5));
        await logger.WaitForEntryCountAsync(1);
        time.Advance(TimeSpan.FromSeconds(1));
        await transport.WaitForNextCallAsync();
        ResolvedCentralConfig result = await load;

        Assert.Equal(2, result.AttemptCount);
        Assert.Equal(2, transport.CallCount);
    }

    [Fact]
    public async Task Late_fault_from_abandoned_transport_does_not_change_completed_flow_or_diagnostics()
    {
        var time = new FakeTimeProvider();
        var transport = new FakeDistributionTransport();
        TaskCompletionSource<DistributionHttpResponse> abandoned = transport.EnqueueNonCooperativeResponse();
        transport.EnqueueResponse(ValidResponse());
        var logger = new RecordingLogger();
        var resolver = CreateResolver(
            transport,
            time,
            new FixedRetryJitter(TimeSpan.Zero),
            logger);
        using var total = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
        Task<ResolvedCentralConfig> load = resolver.ResolveAsync(
            EnabledBootstrap(),
            time.GetTimestamp(),
            CancellationToken.None,
            total.Token);
        await transport.WaitForNextCallAsync();

        time.Advance(TimeSpan.FromSeconds(5));
        await logger.WaitForEntryCountAsync(1);
        time.Advance(TimeSpan.FromSeconds(1));
        await transport.WaitForNextCallAsync();
        ResolvedCentralConfig result = await load;

        abandoned.SetException(new InvalidOperationException("late-sensitive-marker"));
        await Task.Yield();

        Assert.Equal(2, result.AttemptCount);
        LogEntry warning = Assert.Single(logger.Entries);
        Assert.Null(warning.Exception);
        Assert.DoesNotContain("late-sensitive-marker", warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Caller_preempts_a_late_non_cooperative_failure_or_retryable_status(bool throwFailure)
    {
        var time = new FakeTimeProvider();
        var transport = new FakeDistributionTransport();
        TaskCompletionSource<DistributionHttpResponse> late = transport.EnqueueNonCooperativeResponse();
        var logger = new RecordingLogger();
        var resolver = CreateResolver(transport, time, new FixedRetryJitter(), logger);
        using var caller = new CancellationTokenSource();
        using var total = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
        Task<ResolvedCentralConfig> load = resolver.ResolveAsync(
            EnabledBootstrap(),
            time.GetTimestamp(),
            caller.Token,
            total.Token);
        await transport.WaitForNextCallAsync();

        caller.Cancel();
        if (throwFailure)
        {
            late.SetException(new HttpRequestException("credential-like-marker"));
        }
        else
        {
            late.SetResult(Response(HttpStatusCode.ServiceUnavailable));
        }

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);

        Assert.Equal(caller.Token, exception.CancellationToken);
        Assert.Equal(1, transport.CallCount);
        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData(nameof(OwnedCancellationKind.Caller))]
    [InlineData(nameof(OwnedCancellationKind.TotalTimeout))]
    [InlineData(nameof(OwnedCancellationKind.AttemptTimeout))]
    public async Task Owned_cancellation_completes_while_downstream_callback_remains_blocked(
        string cancellationName)
    {
        OwnedCancellationKind cancellationKind =
            Enum.Parse<OwnedCancellationKind>(cancellationName);
        var time = new FakeTimeProvider();
        var transport = new FakeDistributionTransport();
        if (cancellationKind == OwnedCancellationKind.AttemptTimeout)
        {
            for (int index = 0; index < 3; index++)
            {
                transport.EnqueueFailure(new HttpRequestException("earlier"));
            }
        }

        using var callbackGate = new BlockingCallbackGate();
        TaskCompletionSource<DistributionHttpResponse> late =
            transport.EnqueueNonCooperativeResponse(callbackGate.Block);
        using var caller = new CancellationTokenSource();
        using var total = new CancellationTokenSource();
        var logger = new RecordingLogger();
        Task<ResolvedCentralConfig> load = CreateResolver(
                transport,
                time,
                new FixedRetryJitter(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero),
                logger)
            .ResolveAsync(
                EnabledBootstrap(),
                time.GetTimestamp(),
                caller.Token,
                total.Token);
        await transport.WaitForNextCallAsync();
        if (cancellationKind == OwnedCancellationKind.AttemptTimeout)
        {
            await logger.WaitForEntryCountAsync(1);
            time.Advance(TimeSpan.FromSeconds(1));
            await transport.WaitForNextCallAsync();
            await logger.WaitForEntryCountAsync(2);
            time.Advance(TimeSpan.FromSeconds(2));
            await transport.WaitForNextCallAsync();
            await logger.WaitForEntryCountAsync(3);
            time.Advance(TimeSpan.FromSeconds(4));
            await transport.WaitForNextCallAsync();
        }

        Task cancellationTask = cancellationKind switch
        {
            OwnedCancellationKind.Caller => Task.Run(caller.Cancel),
            OwnedCancellationKind.TotalTimeout => Task.Run(total.Cancel),
            OwnedCancellationKind.AttemptTimeout => Task.Run(
                () => time.Advance(TimeSpan.FromSeconds(5))),
            _ => throw new InvalidOperationException("An owned cancellation kind is required.")
        };
        Exception? observedException = null;

        try
        {
            await callbackGate.WaitUntilEnteredAsync();
            observedException = await Record.ExceptionAsync(async () =>
            {
                _ = await load.WaitAsync(TimeSpan.FromSeconds(2));
            });
        }
        finally
        {
            callbackGate.Release();
            await cancellationTask.WaitAsync(TimeSpan.FromSeconds(5));
            late.TrySetResult(ValidResponse());
        }

        if (cancellationKind == OwnedCancellationKind.Caller)
        {
            OperationCanceledException exception =
                Assert.IsAssignableFrom<OperationCanceledException>(observedException);
            Assert.Equal(caller.Token, exception.CancellationToken);
        }
        else
        {
            CentralConfigLoadException exception =
                Assert.IsType<CentralConfigLoadException>(observedException);
            Assert.Equal(
                cancellationKind == OwnedCancellationKind.TotalTimeout
                    ? CentralConfigFailureKind.TotalTimeout
                    : CentralConfigFailureKind.AttemptTimeout,
                exception.FailureKind);
            Assert.Equal(
                cancellationKind == OwnedCancellationKind.TotalTimeout ? 1 : 4,
                exception.AttemptCount);
        }
    }

    private static RetryingDistributionResolver CreateResolver(
        IDistributionTransport transport,
        TimeProvider timeProvider,
        IRetryJitter jitter,
        RecordingLogger logger,
        IDistributionResponseParser? responseParser = null) =>
        new(
            transport,
            timeProvider,
            jitter,
            new StartupLogEmitter(logger),
            responseParser ?? DistributionResponseParser.Instance);

    private static EnabledCentralConfigBootstrap EnabledBootstrap(
        IReadOnlyList<string?>? required = null,
        IReadOnlyList<string?>? fallback = null)
    {
        Assert.True(KeySelection.TryCreate(
            required ?? ["Demo:Required"],
            fallback ?? ["Demo:Fallback"],
            out KeySelection? selection));
        return new EnabledCentralConfigBootstrap(
            "UAT",
            new Uri("https://central/api/v1/distribution/configurations/resolve"),
            selection!);
    }

    private static DistributionHttpResponse ValidResponse() => Response(
        HttpStatusCode.OK,
        "{\"values\":{\"Demo:Required\":\"central\"},\"missingKeys\":[\"Demo:Fallback\"]}");

    private static DistributionHttpResponse Response(HttpStatusCode statusCode, string body = "") =>
        new(statusCode, Encoding.UTF8.GetBytes(body));

    private static void AssertWarning(
        LogEntry entry,
        int attemptCount,
        CentralConfigFailureKind failureKind,
        HttpStatusCode? httpStatusCode,
        double delayMs,
        string? centralErrorCode = null,
        string? centralTraceId = null)
    {
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, entry.Level);
        KeyValuePair<string, object?>[] fields = entry.Fields
            .Where(field => field.Key != "{OriginalFormat}")
            .ToArray();
        object? expectedStatusCode = httpStatusCode is null ? null : (int)httpStatusCode.Value;
        Assert.Collection(
            fields,
            field =>
            {
                Assert.Equal("AttemptCount", field.Key);
                Assert.Equal(attemptCount, Assert.IsType<int>(field.Value));
            },
            field =>
            {
                Assert.Equal("FailureKind", field.Key);
                Assert.Equal(failureKind, Assert.IsType<CentralConfigFailureKind>(field.Value));
            },
            field =>
            {
                Assert.Equal("HttpStatusCode", field.Key);
                Assert.Equal(expectedStatusCode, field.Value);
            },
            field =>
            {
                Assert.Equal("DelayMs", field.Key);
                Assert.Equal(delayMs, Assert.IsType<double>(field.Value));
            },
            field =>
            {
                Assert.Equal("CentralErrorCode", field.Key);
                Assert.Equal(centralErrorCode, field.Value);
            },
            field =>
            {
                Assert.Equal("CentralTraceId", field.Key);
                Assert.Equal(centralTraceId, field.Value);
            });
    }
}
