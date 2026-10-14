using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

public sealed class PocProcessEnvironmentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Central_final_ownership_barrier_rechecks_deadline_and_cancellation(
        bool cancelCaller)
    {
        (Task proof, bool held, _) = await Task.Run(() => HoldFinalManagementContinuation(cancelCaller));
        Assert.True(held, "The final proof continuation must be held to exercise the barrier.");

        if (cancelCaller)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => proof);
        }
        else
        {
            await Assert.ThrowsAsync<TimeoutException>(() => proof);
        }
    }

    [Fact]
    public async Task Central_outer_startup_timeout_does_not_wait_for_pending_exit_output_marker()
    {
        (Task proof, bool held, bool completedBeforeMarker) = await Task.Run(
            () => HoldFinalManagementContinuation(cancelCaller: false, pendingMarker: true));

        Assert.True(held, "The final successful Management continuation must reach the deadline barrier.");
        Assert.True(completedBeforeMarker,
            "The outer startup timeout must complete while exit-output classification is still pending.");
        await Assert.ThrowsAsync<TimeoutException>(() => proof);
    }

    private static (Task Proof, bool Held, bool CompletedBeforeMarker) HoldFinalManagementContinuation(
        bool cancelCaller,
        bool pendingMarker = false)
    {
        using var context = new FinalManagementContinuationContext();
        using var handler = new ControlledCentralProofHandler();
        using var client = new HttpClient(handler);
        using Process currentProcess = Process.GetCurrentProcess();
        using var cancellation = new CancellationTokenSource();
        var delayedMarker = new TaskCompletionSource<bool>();
        var process = (ChildProcess)RuntimeHelpers.GetUninitializedObject(typeof(ChildProcess));
        var environment = (PocProcessEnvironment)RuntimeHelpers.GetUninitializedObject(
            typeof(PocProcessEnvironment));
        SetField(process, "process", currentProcess);
        SetField(process, "processExitTask", new TaskCompletionSource().Task);
        SetField(process, "name", "Final ownership barrier regression");
        SetField(process, "outputGate", new object());
        SetField(process, "output", new ChildProcessOutputBuffer(1024));
        SetField(process, "outputChanged", new TaskCompletionSource());
        SetField(environment, "httpClient", client);
        SetField(environment, "<CentralBaseAddress>k__BackingField", new Uri("http://127.0.0.1:1/"));
        SetField(environment, "<CentralEnvironment>k__BackingField", "Demo");
        MethodInfo method = typeof(PocProcessEnvironment).GetMethod(
            pendingMarker ? "WaitForCentralReadinessAsync" : "WaitForCentralProbeAndManagementProofAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        Task proof;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            object[] arguments = pendingMarker
                ? [process, ControlledCentralProofHandler.InstanceId, cancellation.Token]
                : [process, ControlledCentralProofHandler.InstanceId, deadline,
                    Task.FromResult(false), cancellation.Token];
            proof = (Task)method.Invoke(environment, arguments)!;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        Assert.True(handler.ManagementRequested);
        Task<bool>? originalMarker = null;
        if (pendingMarker)
        {
            originalMarker = (Task<bool>)GetStateMachineField(proof, "addressInUseTask");
            SetStateMachineField(proof, "addressInUseTask", delayedMarker.Task);
        }

        handler.CompleteManagement();
        bool held = false;
        bool completedBeforeMarker = false;
        while (!proof.IsCompleted || originalMarker is { IsCompleted: false })
        {
            Assert.True(context.Continuations.TryTake(out var continuation, TimeSpan.FromSeconds(3)),
                "The expected proof continuation was not queued.");
            // Hold only the proof method's continuation after its guarded Management result.
            object? continuationTarget = (continuation.State as Delegate)?.Target;
            if (continuationTarget?.GetType().FullName?.Contains(
                "PocProcessEnvironment+<WaitForCentralProbeAndManagementProofAsync>d__",
                StringComparison.Ordinal) == true)
            {
                AssertManagementSucceeded(continuationTarget);
                held = true;
                if (pendingMarker)
                {
                    // The outer method's deadline is intentionally not a configurable API.
                    // Expire its inner proof's captured budget only after Management succeeded.
                    SetStateMachineField(continuationTarget, "deadline", DateTimeOffset.UtcNow.AddMilliseconds(-1));
                }
                else if (cancelCaller)
                {
                    cancellation.Cancel();
                }
                else
                {
                    TimeSpan wait = deadline - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(50);
                    if (wait > TimeSpan.Zero)
                    {
                        Thread.Sleep(wait);
                    }
                }
            }

            SynchronizationContext.SetSynchronizationContext(null);
            continuation.Callback(continuation.State);
            if (pendingMarker && continuationTarget?.GetType().FullName?.Contains(
                "PocProcessEnvironment+<WaitForCentralReadinessAsync>d__",
                StringComparison.Ordinal) == true)
            {
                Assert.False(delayedMarker.Task.IsCompleted);
                completedBeforeMarker = proof.IsCompleted;
                // Always release the controlled classification after observing the outcome.
                // This keeps the RED path bounded too and lets normal cancellation finish.
                delayedMarker.SetResult(false);
            }
        }

        if (originalMarker is not null)
        {
            ProofRequestGuard.ObserveIncompleteOperation(originalMarker);
        }

        return (proof, held, completedBeforeMarker);
    }

    private static void AssertManagementSucceeded(object continuationTarget)
    {
        object stateMachine = GetStateMachine(continuationTarget);
        FieldInfo awaiterField = stateMachine.GetType().GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(field => field.FieldType == typeof(TaskAwaiter<bool>));
        var awaiter = (TaskAwaiter<bool>)awaiterField.GetValue(stateMachine)!;
        Assert.True(awaiter.IsCompleted, "The guarded Management operation must be completed before holding its continuation.");
        Assert.True(awaiter.GetResult(), "The guarded Management operation must have succeeded.");
    }

    private static object GetStateMachine(object taskBox) =>
        taskBox.GetType().GetField("StateMachine", BindingFlags.Instance | BindingFlags.Public)!
            .GetValue(taskBox)!;

    private static FieldInfo FindStateMachineField(object stateMachine, string name) =>
        stateMachine.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(field => field.Name == name || field.Name.StartsWith($"<{name}>", StringComparison.Ordinal));

    private static object GetStateMachineField(object taskBox, string name)
    {
        object stateMachine = GetStateMachine(taskBox);
        return FindStateMachineField(stateMachine, name).GetValue(stateMachine)!;
    }

    private static void SetStateMachineField(object taskBox, string name, object value)
    {
        object stateMachine = GetStateMachine(taskBox);
        FindStateMachineField(stateMachine, name).SetValue(stateMachine, value);
        taskBox.GetType().GetField("StateMachine", BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(taskBox, stateMachine);
    }

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);

    private sealed class FinalManagementContinuationContext : SynchronizationContext, IDisposable
    {
        internal BlockingCollection<(SendOrPostCallback Callback, object? State)> Continuations { get; } = [];

        public override void Post(SendOrPostCallback callback, object? state) =>
            Continuations.Add((callback, state));

        public void Dispose() => Continuations.Dispose();
    }

    private sealed class ControlledCentralProofHandler : HttpMessageHandler
    {
        internal const string InstanceId = "794c1ca58c1342a6863d81e152777b17";
        private readonly TaskCompletionSource<HttpResponseMessage> managementResponse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool ManagementRequested { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/health/ready")
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""{"status":"ready","instanceId":"{{InstanceId}}"}""",
                        Encoding.UTF8, "application/json")
                };
                response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
                return Task.FromResult(response);
            }

            Assert.Equal("/api/v1/management/configurations/current", request.RequestUri.AbsolutePath);
            ManagementRequested = true;
            return managementResponse.Task;
        }

        internal void CompleteManagement() => managementResponse.SetResult(
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"code":"configuration_not_found"}""",
                    Encoding.UTF8, "application/problem+json")
            });
    }

    [Fact]
    public async Task Central_http_contract_failure_wins_over_an_already_observed_address_marker()
    {
        var contractFailure = new CentralReadinessContractException();
        Task<CentralReadinessObservation> completedHttpOperation =
            Task.FromException<CentralReadinessObservation>(contractFailure);

        CentralReadinessContractException exception =
            await Assert.ThrowsAsync<CentralReadinessContractException>(
                () => PocProcessEnvironment.CompleteCentralHttpOperationAsync(
                    completedHttpOperation,
                    Task.FromResult(true)));

        Assert.Same(contractFailure, exception);
    }

    [Fact]
    public async Task Proof_request_deadline_bounds_the_entire_operation()
    {
        var demoExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();

        TimeoutException exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            ProofRequestGuard.RunAsync(
                    static async cancellationToken =>
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        return 0;
                    },
                    demoExit.Task,
                    TimeSpan.FromMilliseconds(25),
                    "GET",
                    "/api/v1/demo/effective-configuration",
                    "post-readiness proof request",
                    static () => new InvalidOperationException("Demo exited."),
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal(
            "GET /api/v1/demo/effective-configuration timed out during post-readiness proof request.",
            exception.Message);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Proof_request_cancels_the_operation_when_the_owned_demo_exits()
    {
        var demoExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> proofRequest = ProofRequestGuard.RunAsync(
            async cancellationToken =>
            {
                using CancellationTokenRegistration registration = cancellationToken.Register(
                    static state => ((TaskCompletionSource)state!).TrySetResult(),
                    requestCanceled);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            },
            demoExit.Task,
            TimeSpan.FromSeconds(1),
            "GET",
            "/api/v1/demo/effective-configuration",
            "post-readiness proof request",
            static () => new InvalidOperationException("Demo exited during proof request."),
            CancellationToken.None);

        demoExit.TrySetResult();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => proofRequest.WaitAsync(TimeSpan.FromSeconds(2)));
        await requestCanceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Demo exited during proof request.", exception.Message);
    }
}
