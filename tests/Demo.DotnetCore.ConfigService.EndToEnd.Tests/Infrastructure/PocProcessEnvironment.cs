using System.Net;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

internal sealed class PocProcessEnvironment : IAsyncDisposable
{
    private const string CentralReadinessRoute = "/health/ready";
    private const string CentralManagementProofRoute =
        "/api/v1/management/configurations/current?environment={0}&key=Demo%3AReadiness";
    private const string DemoConfigurationRoute = "/api/v1/demo/effective-configuration";
    private const string DemoUnsafeRootConfigurationRoute =
        "/api/v1/demo/unsafe/root-configuration";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollingDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ExitedOutputTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AddressInUseClassificationTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProofRequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object lifecycleGate = new();
    private readonly SemaphoreSlim centralLifecycleGate = new(1, 1);
    private readonly SemaphoreSlim demoLifecycleGate = new(1, 1);
    private readonly PackagedDemoFixture fixture;
    private readonly string scenarioName;
    private readonly TemporaryDirectory scenarioDirectory;
    private readonly HttpClient httpClient;
    private readonly RetiringResourceOwner<ChildProcess> centralProcessOwner = new();
    private readonly RetiringResourceOwner<ChildProcess> demoProcessOwner = new();
    private LoopbackPortReservation? centralPortReservation;
    private LoopbackPortReservation? demoPortReservation;
    private ManagementClient? management;
    private Task? disposeTask;
    private bool centralEndpointPublished;
    private int centralLaunchCount;
    private int demoLaunchCount;

    private PocProcessEnvironment(
        PackagedDemoFixture fixture,
        string scenarioName,
        string centralEnvironment,
        TemporaryDirectory scenarioDirectory,
        LoopbackPortReservation centralPortReservation,
        LoopbackPortReservation demoPortReservation)
    {
        this.fixture = fixture;
        this.scenarioName = scenarioName;
        this.scenarioDirectory = scenarioDirectory;
        this.centralPortReservation = centralPortReservation;
        this.demoPortReservation = demoPortReservation;
        CentralEnvironment = centralEnvironment;
        CentralBaseAddress = CreateLoopbackAddress(centralPortReservation.Port);
        DemoBaseAddress = CreateLoopbackAddress(demoPortReservation.Port);
        httpClient = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    internal string CentralEnvironment { get; }

    internal Uri CentralBaseAddress { get; private set; }

    internal Uri DemoBaseAddress { get; private set; }

    internal ManagementClient Management => management ??= new ManagementClient(CentralBaseAddress);

    internal IReadOnlyList<ChildProcessOutputLine> DemoOutput
        => demoProcessOwner.Owned?.SnapshotOutput() ?? [];

    internal static async Task RunAsync(
        PackagedDemoFixture fixture,
        string scenarioName,
        Func<PocProcessEnvironment, Task> scenario,
        string? centralEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioName);
        ArgumentNullException.ThrowIfNull(scenario);

        PocProcessEnvironment environment = await CreateAsync(
            fixture,
            scenarioName,
            centralEnvironment ?? "Demo");
        Exception? primaryException = null;

        try
        {
            await scenario(environment);
        }
        catch (Exception exception)
        {
            primaryException = exception;
        }

        Exception? cleanupException = null;
        try
        {
            await environment.DisposeAsync();
        }
        catch (Exception exception)
        {
            cleanupException = exception;
        }

        if (primaryException is not null && cleanupException is not null)
        {
            throw new AggregateException(primaryException, cleanupException);
        }

        if (primaryException is not null)
        {
            ExceptionDispatchInfo.Capture(primaryException).Throw();
        }

        if (cleanupException is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupException).Throw();
        }
    }

    internal async Task StartCentralAsync(CancellationToken cancellationToken = default)
    {
        await centralLifecycleGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (lifecycleGate)
            {
                if (disposeTask is not null)
                {
                    throw new InvalidOperationException(
                        $"Scenario '{scenarioName}' cannot start Central during disposal.");
                }

                if (centralEndpointPublished || centralProcessOwner.Owned is not null)
                {
                    throw new InvalidOperationException(
                        $"Scenario '{scenarioName}' cannot start Central from its current lifecycle state.");
                }
            }

            for (int attempt = 1; attempt <= 5; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (centralPortReservation is null)
                {
                    ReplaceCentralPortReservation();
                }

                LoopbackPortReservation reservation = centralPortReservation!;
                centralPortReservation = null;
                reservation.Dispose();
                CentralLaunch launch = CreateCentralLaunch();
                ChildProcess launchedProcess = ChildProcess.Start(launch.ProcessOptions);
                centralProcessOwner.Attach(launchedProcess);

                try
                {
                    await WaitForCentralReadinessAsync(
                        launchedProcess,
                        launch.ExpectedInstanceId,
                        cancellationToken);
                    lock (lifecycleGate)
                    {
                        centralEndpointPublished = true;
                    }

                    return;
                }
                catch (CentralAddressInUseException) when (attempt < 5)
                {
                    await centralProcessOwner.RetireAsync(
                        CleanupOwnedProcessAsync,
                        cancellationToken);
                    ReplaceCentralPortReservation();
                    continue;
                }
            }
        }
        finally
        {
            centralLifecycleGate.Release();
        }
    }

    internal Task StopCentralAsync(CancellationToken cancellationToken = default) =>
        StopCentralCoreAsync(forCleanup: false, cancellationToken);

    internal async Task RestartCentralAsync(CancellationToken cancellationToken = default)
    {
        await centralLifecycleGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (lifecycleGate)
            {
                if (disposeTask is not null)
                {
                    throw new InvalidOperationException(
                        $"Scenario '{scenarioName}' cannot restart Central during disposal.");
                }

                if (!centralEndpointPublished
                    || centralProcessOwner.Active is not null
                    || centralProcessOwner.Retiring is not null)
                {
                    throw new InvalidOperationException(
                        $"Scenario '{scenarioName}' cannot restart Central from its current lifecycle state.");
                }
            }

            CentralLaunch launch = CreateCentralLaunch();
            ChildProcess launchedProcess = ChildProcess.Start(launch.ProcessOptions);
            centralProcessOwner.Attach(launchedProcess);
            try
            {
                await WaitForCentralReadinessAsync(
                    launchedProcess,
                    launch.ExpectedInstanceId,
                    cancellationToken);
            }
            catch (Exception restartFailure)
            {
                bool addressInUse = restartFailure is CentralAddressInUseException;
                Exception? cleanupFailure = null;
                try
                {
                    await centralProcessOwner.RetireAsync(
                        CleanupOwnedProcessAsync,
                        CancellationToken.None);
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                }

                Exception reportedFailure = addressInUse
                    ? new InvalidOperationException(
                        $"Scenario '{scenarioName}' could not restart Central because its published address is in use.")
                    : restartFailure;
                if (cleanupFailure is not null)
                {
                    throw new AggregateException(reportedFailure, cleanupFailure);
                }

                ExceptionDispatchInfo.Capture(reportedFailure).Throw();
                return;
            }
        }
        finally
        {
            centralLifecycleGate.Release();
        }
    }

    internal Task StartDemoAndWaitUntilReadyAsync(
        CancellationToken cancellationToken = default) =>
        StartDemoAndWaitUntilReadyAsync(null, cancellationToken);

    internal async Task StartDemoAndWaitUntilReadyAsync(
        DemoLaunchOptions? options,
        CancellationToken cancellationToken = default)
    {
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            await StartDemoAsync(options, cancellationToken);
            ChildProcess launchedProcess = GetDemoProcess("Demo readiness");

            try
            {
                await WaitForDemoReadinessAsync(launchedProcess, cancellationToken);
                return;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                bool addressInUse = attempt < 5
                    && await launchedProcess.WaitForAddressInUseOutputAsync(
                        AddressInUseClassificationTimeout,
                        cancellationToken);
                if (addressInUse)
                {
                    await StopDemoAsync(cancellationToken);
                    continue;
                }

                throw;
            }
        }
    }

    internal async Task StartDemoAsync(
        DemoLaunchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new DemoLaunchOptions();
        DemoLaunchOptionsValidator.Validate(options);

        await demoLifecycleGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (lifecycleGate)
            {
                if (disposeTask is not null)
                {
                    throw new InvalidOperationException(
                        $"Scenario '{scenarioName}' cannot start Demo during disposal.");
                }

                if (!centralEndpointPublished)
                {
                    throw new InvalidOperationException(
                        $"Scenario '{scenarioName}' must publish a Central endpoint before Demo launch.");
                }
            }

            try
            {
                await demoProcessOwner.PrepareForLaunchAsync(
                    static process => process.HasExited,
                    CleanupOwnedProcessAsync,
                    cancellationToken);

                if (demoPortReservation is null)
                {
                    ReplaceDemoPortReservation();
                }

                ChildProcessStartOptions startOptions = CreateDemoStartOptions(options);
                LoopbackPortReservation reservation = demoPortReservation!;
                demoPortReservation = null;
                reservation.Dispose();
                ChildProcess launchedProcess = ChildProcess.Start(startOptions);
                demoProcessOwner.Attach(launchedProcess);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Scenario '{scenarioName}' failed during Demo launch.");
            }
        }
        finally
        {
            demoLifecycleGate.Release();
        }
    }

    internal Task WaitForDemoReadinessAsync(CancellationToken cancellationToken = default) =>
        WaitForDemoReadinessAsync(GetDemoProcess("Demo readiness"), cancellationToken);

    internal async Task StopDemoAsync(CancellationToken cancellationToken = default)
    {
        await demoLifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await demoProcessOwner.RetireAsync(
                CleanupOwnedProcessAsync,
                cancellationToken);
        }
        finally
        {
            demoLifecycleGate.Release();
        }
    }

    internal async Task<StartupMarker> WaitForDemoMarkerAsync(
        Func<StartupMarker, bool> predicate,
        string purpose,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        ChildProcess ownedDemo = GetDemoProcess(purpose);
        StartupMarker? matchingMarker = null;
        await ownedDemo.WaitForOutputAsync(
            purpose,
            line =>
            {
                if (!StartupMarker.TryParse(line.Text, out StartupMarker? marker)
                    || marker is null
                    || !predicate(marker))
                {
                    return false;
                }

                matchingMarker = marker;
                return true;
            },
            timeout,
            cancellationToken);

        return matchingMarker!;
    }

    internal Task<ChildProcessExit> WaitForDemoExitAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        return GetDemoProcess("Demo exit").WaitForExitAsync(timeout, cancellationToken);
    }

    internal async Task<EffectiveConfigurationResponse> GetEffectiveConfigurationAsync(
        CancellationToken cancellationToken = default) =>
        (await GetEffectiveConfigurationResultAsync(cancellationToken)).Body;

    internal Task<DemoHttpResult<EffectiveConfigurationResponse>>
        GetEffectiveConfigurationResultAsync(CancellationToken cancellationToken = default) =>
        GetDemoHttpResultAsync<EffectiveConfigurationResponse>(
            DemoConfigurationRoute,
            cancellationToken);

    internal Task<DemoHttpResult<UnsafeRootConfigurationResponse>>
        GetUnsafeRootConfigurationResultAsync(CancellationToken cancellationToken = default) =>
        GetDemoHttpResultAsync<UnsafeRootConfigurationResponse>(
            DemoUnsafeRootConfigurationRoute,
            cancellationToken);

    private Task<DemoHttpResult<TBody>> GetDemoHttpResultAsync<TBody>(
        string route,
        CancellationToken cancellationToken)
    {
        ChildProcess ownedDemo = GetDemoProcess("post-readiness proof request");

        return ProofRequestGuard.RunAsync(
            token => GetDemoHttpResultCoreAsync<TBody>(route, token),
            ownedDemo.WaitForExitSignalAsync(),
            ProofRequestTimeout,
            "GET",
            route,
            "post-readiness proof request",
            () => new InvalidOperationException(
                $"Process '{ownedDemo.Name}' exited with code {ownedDemo.ExitCode} during post-readiness proof request."),
            cancellationToken);
    }

    private async Task<DemoHttpResult<TBody>> GetDemoHttpResultCoreAsync<TBody>(
        string route,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(DemoBaseAddress, route));
            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            return await DemoHttpResponseReader.ReadAsync<TBody>(
                response,
                route,
                cancellationToken);
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException(
                $"GET {route} failed during post-readiness proof request.");
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (lifecycleGate)
        {
            disposeTask ??= DisposeCoreAsync();
            return new ValueTask(disposeTask);
        }
    }

    private static async Task<PocProcessEnvironment> CreateAsync(
        PackagedDemoFixture fixture,
        string scenarioName,
        string centralEnvironment)
    {
        TemporaryDirectory scenarioDirectory = TemporaryDirectory.Create("config-service-e2e-scenario-");
        LoopbackPortReservation? centralReservation = null;
        LoopbackPortReservation? demoReservation = null;

        try
        {
            centralReservation = LoopbackPortAllocator.Reserve();
            demoReservation = LoopbackPortAllocator.Reserve();
            return new PocProcessEnvironment(
                fixture,
                scenarioName,
                centralEnvironment,
                scenarioDirectory,
                centralReservation,
                demoReservation);
        }
        catch
        {
            demoReservation?.Dispose();
            centralReservation?.Dispose();
            await scenarioDirectory.DisposeAsync();
            throw;
        }
    }

    private CentralLaunch CreateCentralLaunch()
    {
        int launchNumber = ++centralLaunchCount;
        string centralOutputDirectory = Path.GetDirectoryName(fixture.CentralAssemblyPath)!;
        string databasePath = Path.Combine(scenarioDirectory.Path, "central-config.db");
        // Generate inside this per-attempt factory so a retry cannot reuse stale
        // address-ownership evidence from an earlier Central process.
        string expectedInstanceId = Guid.NewGuid().ToString("N");
        string[] arguments =
        [
            fixture.CentralAssemblyPath,
            "--contentRoot",
            centralOutputDirectory,
            "--urls",
            CentralBaseAddress.AbsoluteUri.TrimEnd('/'),
            "--ConnectionStrings:CentralConfigDatabase",
            $"Data Source={databasePath};Pooling=False"
        ];
        IReadOnlyDictionary<string, string> environment =
            ProcessEnvironment.CreateApplication(
                scenarioDirectory.Path,
                new Dictionary<string, string>
                {
                    ["CentralHealth__InstanceId"] = expectedInstanceId
                });

        return new CentralLaunch(
            new ChildProcessStartOptions(
                $"Central for {scenarioName}",
                fixture.DotnetHostPath,
                centralOutputDirectory,
                Path.Combine(scenarioDirectory.Path, $"central-{launchNumber:D2}.log"),
                arguments,
                environment),
            expectedInstanceId);
    }

    private ChildProcessStartOptions CreateDemoStartOptions(DemoLaunchOptions options)
    {
        int launchNumber = ++demoLaunchCount;
        string demoOutputDirectory = Path.GetDirectoryName(fixture.DemoAssemblyPath)!;
        var arguments = new List<string>
        {
            fixture.DemoAssemblyPath,
            "--contentRoot",
            demoOutputDirectory,
            "--urls",
            DemoBaseAddress.AbsoluteUri.TrimEnd('/'),
            "--CentralConfig:Enabled",
            "true",
            "--CentralConfig:BaseAddress",
            CentralBaseAddress.AbsoluteUri,
            "--CentralConfig:Environment",
            options.CentralEnvironment ?? CentralEnvironment
        };

        if (options.CommandLineOverrides is not null)
        {
            foreach ((string name, string value) in options.CommandLineOverrides)
            {
                arguments.Add($"--{name}");
                arguments.Add(value);
            }
        }

        IReadOnlyDictionary<string, string> environment =
            ProcessEnvironment.CreateApplication(
                scenarioDirectory.Path,
                options.EnvironmentOverrides);

        return new ChildProcessStartOptions(
            $"Demo for {scenarioName}",
            fixture.DotnetHostPath,
            demoOutputDirectory,
            Path.Combine(scenarioDirectory.Path, $"demo-{launchNumber:D2}.log"),
            arguments,
            environment);
    }

    private async Task WaitForCentralReadinessAsync(
        ChildProcess process,
        string expectedInstanceId,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + StartupTimeout;
        using var coordination = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        Task<bool> addressInUseTask = process.WaitForAddressInUseOutputAsync(
            GetRemaining(deadline, "Central readiness", process),
            coordination.Token);

        try
        {
            await WaitForCentralProbeAndManagementProofAsync(
                process,
                expectedInstanceId,
                deadline,
                addressInUseTask,
                coordination.Token);
        }
        catch (CentralOwnedProcessExitedException)
        {
            if (await addressInUseTask)
            {
                throw new CentralAddressInUseException();
            }

            throw;
        }
        catch (TimeoutException)
        {
            // The startup budget is exhausted; output drain remains owned by retirement.
            await ThrowIfAddressInUseObservedAsync(addressInUseTask);
            throw;
        }
        finally
        {
            coordination.Cancel();
            ProofRequestGuard.ObserveIncompleteOperation(addressInUseTask);
        }
    }

    private async Task WaitForCentralProbeAndManagementProofAsync(
        ChildProcess process,
        string expectedInstanceId,
        DateTimeOffset deadline,
        Task<bool> addressInUseTask,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            // Compute the shared deadline outside the retry catch.
            TimeSpan timeout = GetRequestTimeout(
                GetRemaining(deadline, "Central readiness", process));
            try
            {
                CentralReadinessObservation observation =
                    await CompleteCentralHttpOperationAsync(
                        ProofRequestGuard.RunAsync(
                            async token =>
                            {
                                using var request = new HttpRequestMessage(
                                    HttpMethod.Get,
                                    new Uri(CentralBaseAddress, CentralReadinessRoute));
                                using HttpResponseMessage response = await httpClient.SendAsync(
                                    request,
                                    HttpCompletionOption.ResponseHeadersRead,
                                    token);
                                return await CentralReadinessResponseReader.ReadAsync(
                                    response,
                                    expectedInstanceId,
                                    token);
                            },
                            process.WaitForExitSignalAsync(),
                            timeout,
                            "GET",
                            CentralReadinessRoute,
                            "Central readiness",
                            () => new CentralOwnedProcessExitedException(
                                $"Process '{process.Name}' exited with code {process.ExitCode} during Central readiness."),
                            cancellationToken),
                        addressInUseTask);

                if (observation == CentralReadinessObservation.ForeignInstance)
                {
                    throw new CentralAddressInUseException();
                }

                if (observation == CentralReadinessObservation.Ready)
                {
                    break;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TimeoutException)
            {
            }

            await DelayUntilNextPollAsync(deadline, cancellationToken);
        }

        string route = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            CentralManagementProofRoute,
            Uri.EscapeDataString(CentralEnvironment));

        while (true)
        {
            TimeSpan timeout = GetRequestTimeout(
                GetRemaining(deadline, "Central Management proof", process));
            try
            {
                await CompleteCentralHttpOperationAsync(
                    ProofRequestGuard.RunAsync(
                        async token =>
                        {
                            using var request = new HttpRequestMessage(
                                HttpMethod.Get,
                                new Uri(CentralBaseAddress, route));
                            using HttpResponseMessage response = await httpClient.SendAsync(
                                request,
                                HttpCompletionOption.ResponseHeadersRead,
                                token);

                            if (response.StatusCode != HttpStatusCode.NotFound)
                            {
                                throw new InvalidOperationException(
                                    $"GET {route} returned status {(int)response.StatusCode} during Central Management proof.");
                            }

                            string? mediaType = response.Content.Headers.ContentType?.MediaType;
                            if (!string.Equals(
                                mediaType,
                                "application/problem+json",
                                StringComparison.OrdinalIgnoreCase))
                            {
                                throw new InvalidOperationException(
                                    $"GET {route} returned status {(int)response.StatusCode} with an invalid media type during Central Management proof.");
                            }

                            CentralProblemResponse? problem;
                            try
                            {
                                problem = await response.Content.ReadFromJsonAsync<CentralProblemResponse>(
                                    JsonOptions,
                                    token);
                            }
                            catch (JsonException exception)
                            {
                                throw new InvalidOperationException(
                                    $"GET {route} returned status {(int)response.StatusCode} with an invalid response contract during Central Management proof.",
                                    exception);
                            }

                            if (!string.Equals(
                                problem?.Code,
                                "configuration_not_found",
                                StringComparison.Ordinal))
                            {
                                throw new InvalidOperationException(
                                    $"GET {route} returned status {(int)response.StatusCode} with an unexpected problem code during Central Management proof.");
                            }

                            return true;
                        },
                        process.WaitForExitSignalAsync(),
                        timeout,
                        "GET",
                        route,
                        "Central Management proof",
                        () => new CentralOwnedProcessExitedException(
                            $"Process '{process.Name}' exited with code {process.ExitCode} during Central Management proof."),
                        cancellationToken),
                    addressInUseTask);
                break;
            }
            catch (HttpRequestException)
            {
            }
            catch (TimeoutException)
            {
            }

            await DelayUntilNextPollAsync(deadline, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        TimeSpan finalRemaining = GetRemaining(deadline, "Central Management proof", process);
        if (process.HasExited)
        {
            // Bound readiness's exit observation; retirement still owns the complete drain.
            await process.WaitForExitAsync(finalRemaining, cancellationToken);
            await ThrowIfExitedAsync(process, "Central Management proof", cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        _ = GetRemaining(deadline, "Central Management proof", process);
    }

    internal static async Task<TResult> CompleteCentralHttpOperationAsync<TResult>(
        Task<TResult> httpOperation,
        Task<bool> addressInUseTask)
    {
        ArgumentNullException.ThrowIfNull(httpOperation);
        ArgumentNullException.ThrowIfNull(addressInUseTask);

        try
        {
            return await httpOperation;
        }
        catch (Exception exception)
            when (exception is HttpRequestException or TimeoutException)
        {
            // Only transport/request-timeout failures may consult markers; a completed
            // contract failure must escape without being masked as address-in-use.
            await ThrowIfAddressInUseObservedAsync(addressInUseTask);
            throw;
        }
    }

    private static async Task ThrowIfAddressInUseObservedAsync(
        Task<bool> addressInUseTask)
    {
        if (addressInUseTask.IsCompletedSuccessfully
            && await addressInUseTask)
        {
            throw new CentralAddressInUseException();
        }
    }

    private async Task WaitForDemoReadinessAsync(
        ChildProcess process,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + StartupTimeout;

        while (true)
        {
            await ThrowIfExitedAsync(process, "Demo readiness", cancellationToken);
            TimeSpan remaining = GetRemaining(deadline, "Demo readiness", process);

            using var requestCancellation = CreateRequestCancellation(
                remaining,
                cancellationToken);
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    new Uri(DemoBaseAddress, DemoConfigurationRoute));
                using HttpResponseMessage response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    requestCancellation.Token);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new InvalidOperationException(
                        $"GET {DemoConfigurationRoute} returned status {(int)response.StatusCode} during Demo readiness.");
                }

                return;
            }
            catch (HttpRequestException)
            {
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            await ThrowIfExitedAsync(process, "Demo readiness", cancellationToken);
            await DelayUntilNextPollAsync(deadline, cancellationToken);
        }
    }

    private static async Task ThrowIfExitedAsync(
        ChildProcess process,
        string purpose,
        CancellationToken cancellationToken)
    {
        if (process.HasExited)
        {
            await process.WaitForOutputAsync(
                purpose,
                static _ => false,
                ExitedOutputTimeout,
                cancellationToken);
        }
    }

    private static TimeSpan GetRemaining(
        DateTimeOffset deadline,
        string purpose,
        ChildProcess process)
    {
        TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException(
                $"Process '{process.Name}' did not complete {purpose} within {StartupTimeout}.");
        }

        return remaining;
    }

    private static TimeSpan GetRequestTimeout(TimeSpan remaining) =>
        remaining < RequestTimeout ? remaining : RequestTimeout;

    private static CancellationTokenSource CreateRequestCancellation(
        TimeSpan remaining,
        CancellationToken cancellationToken)
    {
        var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCancellation.CancelAfter(remaining < RequestTimeout ? remaining : RequestTimeout);
        return requestCancellation;
    }

    private static Task DelayUntilNextPollAsync(
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return Task.CompletedTask;
        }

        TimeSpan delay = remaining < PollingDelay ? remaining : PollingDelay;
        return Task.Delay(delay, cancellationToken);
    }

    private void ReplaceCentralPortReservation()
    {
        centralPortReservation?.Dispose();
        centralPortReservation = LoopbackPortAllocator.Reserve();
        CentralBaseAddress = CreateLoopbackAddress(centralPortReservation.Port);
        management?.Dispose();
        management = null;
    }

    private void ReplaceDemoPortReservation()
    {
        demoPortReservation?.Dispose();
        demoPortReservation = LoopbackPortAllocator.Reserve();
        DemoBaseAddress = CreateLoopbackAddress(demoPortReservation.Port);
    }

    private static Uri CreateLoopbackAddress(int port) =>
        new($"http://127.0.0.1:{port}/", UriKind.Absolute);

    private ChildProcess GetDemoProcess(string purpose)
    {
        return demoProcessOwner.Active
            ?? throw new InvalidOperationException(
                $"Scenario '{scenarioName}' has no active Demo process for {purpose}.");
    }

    private async Task StopCentralCoreAsync(
        bool forCleanup,
        CancellationToken cancellationToken)
    {
        await centralLifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (!forCleanup)
            {
                lock (lifecycleGate)
                {
                    if (disposeTask is not null)
                    {
                        throw new InvalidOperationException(
                            $"Scenario '{scenarioName}' cannot stop Central during disposal.");
                    }

                    if (!centralEndpointPublished
                        || centralProcessOwner.Active is null
                        || centralProcessOwner.Retiring is not null)
                    {
                        throw new InvalidOperationException(
                            $"Scenario '{scenarioName}' cannot stop Central from its current lifecycle state.");
                    }
                }
            }

            await centralProcessOwner.RetireAsync(
                CleanupOwnedProcessAsync,
                cancellationToken);
        }
        finally
        {
            centralLifecycleGate.Release();
        }
    }

    private static async Task<ResourceCleanupOutcome> CleanupOwnedProcessAsync(
        ChildProcess process,
        CancellationToken cancellationToken)
    {
        Exception? stopFailure = null;
        try
        {
            await process.StopAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            stopFailure = exception;
        }

        Exception? disposeFailure = null;
        try
        {
            await process.DisposeAsync();
        }
        catch (Exception exception)
        {
            disposeFailure = exception;
        }

        Exception? failure = (stopFailure, disposeFailure) switch
        {
            (null, null) => null,
            (not null, null) => stopFailure,
            (null, not null) => disposeFailure,
            _ => new AggregateException(stopFailure!, disposeFailure!)
        };

        return new ResourceCleanupOutcome(
            CleanupCompleted: disposeFailure is null,
            Failure: failure);
    }

    private async Task DisposeCoreAsync()
    {
        var cleanupExceptions = new List<Exception>();
        bool resourcesComplete = true;

        CaptureCleanupException(() => management?.Dispose(), cleanupExceptions, ref resourcesComplete);
        management = null;
        CaptureCleanupException(httpClient.Dispose, cleanupExceptions, ref resourcesComplete);

        try
        {
            await StopDemoAsync();
        }
        catch (Exception exception)
        {
            cleanupExceptions.Add(exception);
            resourcesComplete = false;
        }

        try
        {
            await StopCentralCoreAsync(forCleanup: true, CancellationToken.None);
        }
        catch (Exception exception)
        {
            cleanupExceptions.Add(exception);
            resourcesComplete = false;
        }

        CaptureCleanupException(
            () => demoPortReservation?.Dispose(),
            cleanupExceptions,
            ref resourcesComplete);
        demoPortReservation = null;
        CaptureCleanupException(
            () => centralPortReservation?.Dispose(),
            cleanupExceptions,
            ref resourcesComplete);
        centralPortReservation = null;

        if (resourcesComplete)
        {
            try
            {
                await scenarioDirectory.DisposeAsync();
            }
            catch (Exception exception)
            {
                cleanupExceptions.Add(exception);
            }
        }

        ThrowCleanupExceptions(cleanupExceptions);
    }

    private static void CaptureCleanupException(
        Action cleanup,
        ICollection<Exception> exceptions,
        ref bool resourcesComplete)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            exceptions.Add(exception);
            resourcesComplete = false;
        }
    }

    private static void ThrowCleanupExceptions(IReadOnlyCollection<Exception> exceptions)
    {
        if (exceptions.Count == 0)
        {
            return;
        }

        if (exceptions.Count == 1)
        {
            ExceptionDispatchInfo.Capture(exceptions.Single()).Throw();
        }

        throw new AggregateException(exceptions);
    }

    private sealed class CentralOwnedProcessExitedException : InvalidOperationException
    {
        internal CentralOwnedProcessExitedException(string message)
            : base(message)
        {
        }
    }

    private sealed record CentralLaunch(
        ChildProcessStartOptions ProcessOptions,
        string ExpectedInstanceId);

    private sealed class CentralAddressInUseException : InvalidOperationException
    {
        internal CentralAddressInUseException()
            : base("The owned Central process could not bind its assigned address.")
        {
        }
    }
}

internal static class ProofRequestGuard
{
    internal static async Task<TResult> RunAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        Task ownedProcessExit,
        TimeSpan timeout,
        string method,
        string route,
        string lifecyclePhase,
        Func<Exception> createProcessExitException,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(ownedProcessExit);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        ArgumentException.ThrowIfNullOrWhiteSpace(lifecyclePhase);
        ArgumentNullException.ThrowIfNull(createProcessExitException);

        cancellationToken.ThrowIfCancellationRequested();
        if (ownedProcessExit.IsCompleted)
        {
            throw createProcessExitException();
        }

        using var deadline = new CancellationTokenSource(timeout);
        using var stopOperation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        Task<TResult> operationTask = operation(stopOperation.Token);
        Task timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token);
        Task callerCancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        Task completed = await Task.WhenAny(
            operationTask,
            ownedProcessExit,
            timeoutTask,
            callerCancellationTask);

        if (completed == operationTask)
        {
            try
            {
                return await operationTask;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (OperationCanceledException) when (ownedProcessExit.IsCompleted)
            {
                throw createProcessExitException();
            }
            catch (OperationCanceledException)
            {
                throw CreateTimeoutException(method, route, lifecyclePhase);
            }
        }

        stopOperation.Cancel();
        ObserveIncompleteOperation(operationTask);

        if (completed == ownedProcessExit)
        {
            throw createProcessExitException();
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        throw CreateTimeoutException(method, route, lifecyclePhase);
    }

    private static TimeoutException CreateTimeoutException(
        string method,
        string route,
        string lifecyclePhase) =>
        new($"{method} {route} timed out during {lifecyclePhase}.");

    internal static void ObserveIncompleteOperation(Task operation)
    {
        _ = operation.ContinueWith(
            static completedOperation => _ = completedOperation.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
