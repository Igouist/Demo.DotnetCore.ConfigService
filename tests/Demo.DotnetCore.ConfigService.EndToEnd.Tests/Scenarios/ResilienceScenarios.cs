using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Scenarios;

[Collection(EndToEndCollection.Name)]
public sealed class ResilienceScenarios(PackagedDemoFixture fixture)
{
    [Fact]
    public Task J_transport_retry_recovers_after_central_restarts() =>
        PocProcessEnvironment.RunAsync(
            fixture,
            nameof(J_transport_retry_recovers_after_central_restarts),
            async scenario =>
            {
                await scenario.StartCentralAsync();
                await scenario.Management.SetAsync(
                    scenario.CentralEnvironment,
                    "Demo:CentralRequiredMessage",
                    "recovered-value");
                await scenario.StopCentralAsync();

                await scenario.StartDemoAsync();
                StartupMarker retry = await scenario.WaitForDemoMarkerAsync(
                    static marker => marker.Event == "retry" && marker.AttemptCount == 1,
                    "first transport retry marker",
                    TimeSpan.FromSeconds(15));

                await scenario.RestartCentralAsync();
                StartupMarker loaded = await scenario.WaitForDemoMarkerAsync(
                    static marker => marker.Event == "loaded" && marker.AttemptCount is > 1,
                    "post-retry loaded marker",
                    TimeSpan.FromSeconds(15));
                await scenario.WaitForDemoReadinessAsync();

                Assert.Equal("TransportFailure", retry.FailureKind);
                Assert.True(loaded.AttemptCount is > 1);
                Assert.Equal(
                    "recovered-value",
                    (await scenario.GetEffectiveConfigurationAsync())
                        .GetValue("CentralRequiredMessage"));
            });

    [Fact]
    public Task J_unavailable_central_causes_bounded_safe_terminal_failure() =>
        PocProcessEnvironment.RunAsync(
            fixture,
            nameof(J_unavailable_central_causes_bounded_safe_terminal_failure),
            async scenario =>
            {
                const string sentinel = "terminal-output-secret-sentinel";
                await scenario.StartCentralAsync();
                await scenario.StopCentralAsync();
                var stopwatch = Stopwatch.StartNew();
                await scenario.StartDemoAsync(
                    new DemoLaunchOptions(
                        EnvironmentOverrides: new Dictionary<string, string>
                        {
                            ["Demo__LocalOnlyMessage"] = sentinel
                        }));

                StartupMarker failed = await scenario.WaitForDemoMarkerAsync(
                    static marker => marker.Event == "failed",
                    "terminal failed marker",
                    TimeSpan.FromSeconds(45));
                TimeSpan remaining = TimeSpan.FromSeconds(45) - stopwatch.Elapsed;
                Assert.True(remaining > TimeSpan.Zero);
                ChildProcessExit exit = await scenario.WaitForDemoExitAsync(remaining);
                stopwatch.Stop();

                Assert.Equal("TransportFailure", failed.FailureKind);
                Assert.Equal<int?>(4, failed.AttemptCount);
                Assert.Null(failed.HttpStatusCode);
                Assert.NotEqual(0, exit.ExitCode);
                Assert.True(stopwatch.Elapsed <= TimeSpan.FromSeconds(45));
                Assert.DoesNotContain(
                    scenario.DemoOutput,
                    line => line.Text.Contains(sentinel, StringComparison.Ordinal));
                Assert.DoesNotContain(
                    scenario.DemoOutput,
                    static line => StartupMarker.TryParse(line.Text, out StartupMarker? marker)
                        && marker!.Event == "loaded");
            });

    [Fact]
    public Task J_central_lifecycle_rejects_invalid_live_and_stopped_transitions() =>
        PocProcessEnvironment.RunAsync(
            fixture,
            nameof(J_central_lifecycle_rejects_invalid_live_and_stopped_transitions),
            async scenario =>
            {
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => scenario.StopCentralAsync());
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => scenario.RestartCentralAsync());

                await scenario.StartCentralAsync();
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => scenario.StartCentralAsync());
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => scenario.RestartCentralAsync());

                await scenario.StopCentralAsync();
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => scenario.StopCentralAsync());
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => scenario.StartCentralAsync());

                await scenario.RestartCentralAsync();
            });

    [Fact]
    public Task J_restart_rejects_foreign_readiness_on_retained_port() =>
        PocProcessEnvironment.RunAsync(
            fixture,
            nameof(J_restart_rejects_foreign_readiness_on_retained_port),
            async scenario =>
            {
                await scenario.StartCentralAsync();
                Uri publishedAddress = scenario.CentralBaseAddress;
                ManagementClient management = scenario.Management;
                await scenario.StopCentralAsync();

                await using (var foreignListener =
                    ForeignCentralReadinessListener.Start(publishedAddress.Port))
                {
                    InvalidOperationException exception =
                        await Assert.ThrowsAsync<InvalidOperationException>(
                            () => scenario.RestartCentralAsync());

                    Assert.Equal(
                        $"Scenario '{nameof(J_restart_rejects_foreign_readiness_on_retained_port)}' could not restart Central because its published address is in use.",
                        exception.Message);
                    Assert.Null(exception.InnerException);
                    Assert.Equal(
                        "/health/ready",
                        await foreignListener.WaitForFirstRequestTargetAsync());
                }

                Assert.Equal(publishedAddress, scenario.CentralBaseAddress);
                Assert.Same(management, scenario.Management);
                await scenario.RestartCentralAsync();
            });

    [Fact]
    public Task J_restart_preserves_invalid_readiness_contract() =>
        PocProcessEnvironment.RunAsync(
            fixture,
            nameof(J_restart_preserves_invalid_readiness_contract),
            async scenario =>
            {
                await scenario.StartCentralAsync();
                Uri publishedAddress = scenario.CentralBaseAddress;
                await scenario.StopCentralAsync();

                await using (var foreignListener =
                    ForeignCentralReadinessListener.StartWithInvalidContract(
                        publishedAddress.Port))
                {
                    CentralReadinessContractException exception =
                        await Assert.ThrowsAsync<CentralReadinessContractException>(
                            () => scenario.RestartCentralAsync());

                    Assert.Equal(
                        "Central readiness response contract is invalid.",
                        exception.Message);
                    Assert.Equal(
                        "/health/ready",
                        await foreignListener.WaitForFirstRequestTargetAsync());
                }

                await scenario.RestartCentralAsync();
            });

    private sealed class ForeignCentralReadinessListener : IAsyncDisposable
    {
        private const int MaximumRequestHeaderBytes = 8 * 1024;
        private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(2);
        private static readonly byte[] ReadinessResponse = CreateReadinessResponse();
        private readonly TcpListener listener;
        private readonly CancellationTokenSource shutdown = new();
        private readonly Task serveTask;
        private readonly byte[] response;
        private readonly TaskCompletionSource<string> firstRequestTarget =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private ForeignCentralReadinessListener(int port, byte[] response)
        {
            this.response = response;
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            serveTask = ServeAsync();
        }

        internal static ForeignCentralReadinessListener Start(int port) =>
            new(port, ReadinessResponse);

        internal static ForeignCentralReadinessListener StartWithInvalidContract(int port) =>
            new(port, InvalidReadinessResponse);

        internal Task<string> WaitForFirstRequestTargetAsync() =>
            firstRequestTarget.Task.WaitAsync(OperationTimeout);

        public async ValueTask DisposeAsync()
        {
            shutdown.Cancel();
            listener.Stop();

            try
            {
                await serveTask.WaitAsync(OperationTimeout);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
            }
            finally
            {
                shutdown.Dispose();
            }
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!shutdown.IsCancellationRequested)
                {
                    using TcpClient client = await listener.AcceptTcpClientAsync(shutdown.Token);
                    using NetworkStream stream = client.GetStream();
                    using var operation = CancellationTokenSource.CreateLinkedTokenSource(
                        shutdown.Token);
                    operation.CancelAfter(OperationTimeout);

                    try
                    {
                        string? requestTarget = await ReadRequestTargetAsync(stream, operation.Token);
                        if (requestTarget is not null)
                        {
                            firstRequestTarget.TrySetResult(requestTarget);
                            await stream.WriteAsync(response, operation.Token);
                        }
                    }
                    catch (OperationCanceledException) when (!shutdown.IsCancellationRequested)
                    {
                    }
                }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
            }
            catch (SocketException) when (shutdown.IsCancellationRequested)
            {
            }
        }

        private static async Task<string?> ReadRequestTargetAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[MaximumRequestHeaderBytes];
            int length = 0;

            while (length < buffer.Length)
            {
                int bytesRead = await stream.ReadAsync(
                    buffer.AsMemory(length, buffer.Length - length),
                    cancellationToken);
                if (bytesRead == 0)
                {
                    return null;
                }

                length += bytesRead;
                if (HasHeaderTerminator(buffer.AsSpan(0, length)))
                {
                    return ParseRequestTarget(buffer.AsSpan(0, length));
                }
            }

            return null;
        }

        private static string? ParseRequestTarget(ReadOnlySpan<byte> headers)
        {
            int lineEnd = headers.IndexOf("\r\n"u8);
            if (lineEnd < 0)
            {
                return null;
            }

            string[] requestLine = Encoding.ASCII
                .GetString(headers[..lineEnd])
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return requestLine.Length >= 2 ? requestLine[1] : null;
        }

        private static bool HasHeaderTerminator(ReadOnlySpan<byte> bytes)
        {
            ReadOnlySpan<byte> terminator = "\r\n\r\n"u8;
            return bytes.IndexOf(terminator) >= 0;
        }

        private static readonly byte[] InvalidReadinessResponse = CreateResponse(
            """{"status":"ready","instanceId":"not-canonical"}""");

        private static byte[] CreateReadinessResponse() => CreateResponse(
            """{"status":"ready","instanceId":"00000000000000000000000000000001"}""");

        private static byte[] CreateResponse(string body)
        {
            string response =
                "HTTP/1.1 200 OK\r\n"
                + "Content-Type: application/json\r\n"
                + "Cache-Control: no-store\r\n"
                + $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n"
                + "Connection: close\r\n"
                + "\r\n"
                + body;
            return Encoding.UTF8.GetBytes(response);
        }
    }
}
