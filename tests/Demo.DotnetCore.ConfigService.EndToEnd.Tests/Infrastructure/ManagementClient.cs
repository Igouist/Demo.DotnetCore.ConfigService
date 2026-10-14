using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

internal sealed class ManagementClient : IDisposable
{
    private const string SetRoute = "/api/v1/management/configurations/set";
    private const string RemoveRoute = "/api/v1/management/configurations/remove";
    private const string CurrentRoute = "/api/v1/management/configurations/current";
    private const string HistoryRoute = "/api/v1/management/configurations/history";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;
    private readonly TimeSpan requestTimeout;

    internal ManagementClient(Uri centralBaseAddress) :
        this(
            centralBaseAddress,
            new SocketsHttpHandler { UseProxy = false },
            RequestTimeout)
    {
    }

    internal ManagementClient(Uri centralBaseAddress, HttpMessageHandler handler)
        : this(centralBaseAddress, handler, RequestTimeout)
    {
    }

    internal ManagementClient(
        Uri centralBaseAddress,
        HttpMessageHandler handler,
        TimeSpan requestTimeout)
    {
        ArgumentNullException.ThrowIfNull(centralBaseAddress);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(requestTimeout, TimeSpan.Zero);

        httpClient = new HttpClient(handler)
        {
            BaseAddress = centralBaseAddress,
            Timeout = Timeout.InfiniteTimeSpan
        };
        this.requestTimeout = requestTimeout;
    }

    internal async Task<ManagementCommandResponse> SetAsync(
        string environment,
        string key,
        string value,
        CancellationToken cancellationToken = default) =>
        await PostAsync(
            SetRoute,
            new SetConfigurationRequest(environment, key, value),
            cancellationToken);

    internal async Task<ManagementCommandResponse> RemoveAsync(
        string environment,
        string key,
        CancellationToken cancellationToken = default) =>
        await PostAsync(
            RemoveRoute,
            new RemoveConfigurationRequest(environment, key),
            cancellationToken);

    internal Task<CurrentConfigurationResponse> GetCurrentAsync(
        string environment,
        string key,
        CancellationToken cancellationToken = default) =>
        GetAsync<CurrentConfigurationResponse>(
            CurrentRoute,
            environment,
            key,
            cancellationToken);

    internal Task<ConfigurationHistoryResponse> GetHistoryAsync(
        string environment,
        string key,
        CancellationToken cancellationToken = default) =>
        GetAsync<ConfigurationHistoryResponse>(
            HistoryRoute,
            environment,
            key,
            cancellationToken);

    public void Dispose() => httpClient.Dispose();

    private async Task<ManagementCommandResponse> PostAsync<TRequest>(
        string route,
        TRequest request,
        CancellationToken cancellationToken)
    {
        using var requestCancellation = CreateRequestCancellation(cancellationToken);

        try
        {
            using HttpResponseMessage response = await httpClient.PostAsJsonAsync(
                route,
                request,
                JsonOptions,
                requestCancellation.Token);
            return await ValidateResponseAsync(response, route, requestCancellation.Token);
        }
        catch (Exception exception) when (
            exception is HttpRequestException
            || exception is IOException)
        {
            throw new InvalidOperationException($"POST {route} failed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"POST {route} timed out.");
        }
    }

    private async Task<TResponse> GetAsync<TResponse>(
        string route,
        string environment,
        string key,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        string requestRoute = $"{route}?environment={Uri.EscapeDataString(environment)}"
            + $"&key={Uri.EscapeDataString(key)}";
        using var requestCancellation = CreateRequestCancellation(cancellationToken);

        try
        {
            using HttpResponseMessage response = await httpClient.GetAsync(
                requestRoute,
                HttpCompletionOption.ResponseHeadersRead,
                requestCancellation.Token);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new InvalidOperationException(
                    $"GET {route} returned status {(int)response.StatusCode}.");
            }

            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(
                mediaType,
                "application/json",
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"GET {route} returned status {(int)response.StatusCode} with an invalid media type.");
            }

            try
            {
                return await response.Content.ReadFromJsonAsync<TResponse>(
                        JsonOptions,
                        requestCancellation.Token)
                    ?? throw new InvalidOperationException(
                        $"GET {route} returned status {(int)response.StatusCode} without a response contract.");
            }
            catch (Exception exception) when (
                exception is JsonException
                || exception is NotSupportedException)
            {
                throw new InvalidOperationException(
                    $"GET {route} returned status {(int)response.StatusCode} with an invalid response contract.");
            }
        }
        catch (Exception exception) when (
            exception is HttpRequestException
            || exception is IOException)
        {
            throw new InvalidOperationException($"GET {route} failed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"GET {route} timed out.");
        }
    }

    private CancellationTokenSource CreateRequestCancellation(
        CancellationToken cancellationToken)
    {
        var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCancellation.CancelAfter(requestTimeout);
        return requestCancellation;
    }

    private static async Task<ManagementCommandResponse> ValidateResponseAsync(
        HttpResponseMessage response,
        string route,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode != HttpStatusCode.Created)
        {
            throw new InvalidOperationException(
                $"POST {route} returned status {(int)response.StatusCode}.");
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<ManagementCommandResponse>(
                    JsonOptions,
                    cancellationToken)
                ?? throw new InvalidOperationException(
                    $"POST {route} returned status {(int)response.StatusCode} without a response contract.");
        }
        catch (Exception exception) when (
            exception is JsonException
            || exception is NotSupportedException)
        {
            throw new InvalidOperationException(
                $"POST {route} returned status {(int)response.StatusCode} with an invalid response contract.");
        }
    }
}
