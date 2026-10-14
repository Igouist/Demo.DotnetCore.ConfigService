using Demo.DotnetCore.ConfigService.Client.Tests.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Demo.DotnetCore.ConfigService.Client.Tests.Public;

public sealed class CentralConfigHostApplicationBuilderExtensionsTests
{
    [Fact]
    public async Task Public_base_overload_executes_disabled_no_op()
    {
        using var host = new TestHostBuilder(
            "{ \"CentralConfig\": { \"Enabled\": false } }",
            "{}");
        int sourceCount = host.Builder.Configuration.Sources.Count;

        await host.Builder.AddCentralConfigAsync();

        Assert.Equal(sourceCount, host.Builder.Configuration.Sources.Count);
    }

    [Fact]
    public async Task Public_base_overload_forwards_the_exact_caller_token()
    {
        using var host = new TestHostBuilder("{}", "{}");
        using var caller = new CancellationTokenSource();
        caller.Cancel();

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CentralConfigHostApplicationBuilderExtensions.AddCentralConfigAsync(
                (IHostApplicationBuilder)host.Builder,
                caller.Token));

        Assert.Equal(caller.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task Public_logger_overload_forwards_the_exact_caller_token()
    {
        using var host = new TestHostBuilder("{}", "{}");
        var logger = new RecordingLogger();
        using var caller = new CancellationTokenSource();
        caller.Cancel();

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CentralConfigHostApplicationBuilderExtensions.AddCentralConfigAsync(
                (IHostApplicationBuilder)host.Builder,
                (ILogger)logger,
                caller.Token));

        Assert.Equal(caller.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task Public_logger_overload_sends_one_terminal_error_to_the_supplied_logger()
    {
        using var host = new TestHostBuilder("{}", "{}");
        var logger = new RecordingLogger();

        CentralConfigLoadException exception = await Assert.ThrowsAsync<CentralConfigLoadException>(() =>
            CentralConfigHostApplicationBuilderExtensions.AddCentralConfigAsync(
                (IHostApplicationBuilder)host.Builder,
                (ILogger)logger,
                CancellationToken.None));

        Assert.Equal(CentralConfigFailureKind.InvalidBootstrapConfiguration, exception.FailureKind);
        LogEntry error = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Same(exception, error.Exception);
    }

    [Fact]
    public void Public_arguments_are_synchronously_null_guarded_for_both_overloads()
    {
        var logger = new RecordingLogger();

        ArgumentNullException baseBuilderException = Assert.Throws<ArgumentNullException>(() =>
        {
            _ = CentralConfigHostApplicationBuilderExtensions.AddCentralConfigAsync(
                (IHostApplicationBuilder)null!,
                CancellationToken.None);
        });
        Assert.Equal("builder", baseBuilderException.ParamName);

        ArgumentNullException loggerBuilderException = Assert.Throws<ArgumentNullException>(() =>
        {
            _ = CentralConfigHostApplicationBuilderExtensions.AddCentralConfigAsync(
                (IHostApplicationBuilder)null!,
                (ILogger)logger,
                CancellationToken.None);
        });
        Assert.Equal("builder", loggerBuilderException.ParamName);

        using var host = new TestHostBuilder(
            "{ \"CentralConfig\": { \"Enabled\": false } }",
            "{}");
        ArgumentNullException loggerException = Assert.Throws<ArgumentNullException>(() =>
        {
            _ = CentralConfigHostApplicationBuilderExtensions.AddCentralConfigAsync(
                (IHostApplicationBuilder)host.Builder,
                (ILogger)null!,
                CancellationToken.None);
        });
        Assert.Equal("startupLogger", loggerException.ParamName);
    }
}
