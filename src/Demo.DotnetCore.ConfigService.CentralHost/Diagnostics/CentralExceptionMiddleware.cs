using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Demo.DotnetCore.ConfigService.CentralHost;

internal sealed class CentralExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly CentralProblemDetailsWriter _problemDetailsWriter;
    private readonly ILogger<CentralExceptionMiddleware> _logger;

    public CentralExceptionMiddleware(
        RequestDelegate next,
        CentralProblemDetailsWriter problemDetailsWriter,
        ILogger<CentralExceptionMiddleware> logger)
    {
        _next = next;
        _problemDetailsWriter = problemDetailsWriter;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext httpContext)
    {
        try
        {
            await _next(httpContext);
        }
        catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (httpContext.Response.HasStarted)
            {
                LogResponseStartedFailure(httpContext, exception);
                httpContext.Abort();
                return;
            }

            LogFailure(httpContext, exception);
            try
            {
                await _problemDetailsWriter.WriteAsync(
                    httpContext,
                    Map(exception),
                    httpContext.RequestAborted);
            }
            catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception writerException)
            {
                LogProblemDetailsWriterFailure(httpContext, writerException);
                httpContext.Abort();
            }
        }
    }

    private static CentralProblemDescriptor Map(Exception exception) => exception switch
    {
        RequestValidationException validation =>
            new(400, "validation_failed", "One or more request fields are invalid.", validation.Errors),
        ConfigurationEntryNotFoundException =>
            new(404, "configuration_not_found", "The requested Configuration Entry was not found."),
        ConfigurationEntryAlreadyRemovedException =>
            new(409, "configuration_already_removed", "The requested Configuration Entry is already removed."),
        PersistenceBusyException =>
            new(503, "persistence_busy", "The configuration store is temporarily busy."),
        PersistenceFailureException =>
            new(500, "persistence_failure", "The configuration store operation failed."),
        _ =>
            new(500, "internal_error", "An unexpected application failure occurred.")
    };

    private void LogFailure(HttpContext httpContext, Exception exception)
    {
        if (exception is PersistenceFailureException persistenceFailure)
        {
            _logger.LogError(
                exception,
                "Central Host persistence failure. TraceIdentifier: {TraceIdentifier}; PersistenceFailureCategory: {PersistenceFailureCategory}",
                CentralProblemDetailsWriter.GetTraceIdentifier(httpContext),
                persistenceFailure.Category);
        }
        else if (exception is not (RequestValidationException
            or ConfigurationEntryNotFoundException
            or ConfigurationEntryAlreadyRemovedException
            or PersistenceBusyException))
        {
            _logger.LogError(
                exception,
                "Central Host unexpected failure. TraceIdentifier: {TraceIdentifier}",
                CentralProblemDetailsWriter.GetTraceIdentifier(httpContext));
        }
    }

    private void LogResponseStartedFailure(HttpContext httpContext, Exception exception)
    {
        if (exception is PersistenceFailureException persistenceFailure)
        {
            _logger.LogError(
                exception,
                "Central Host failure after the response started. TraceIdentifier: {TraceIdentifier}; PersistenceFailureCategory: {PersistenceFailureCategory}",
                CentralProblemDetailsWriter.GetTraceIdentifier(httpContext),
                persistenceFailure.Category);
            return;
        }

        _logger.LogError(
            exception,
            "Central Host failure after the response started. TraceIdentifier: {TraceIdentifier}",
            CentralProblemDetailsWriter.GetTraceIdentifier(httpContext));
    }

    private void LogProblemDetailsWriterFailure(HttpContext httpContext, Exception writerException) =>
        _logger.LogError(
            writerException,
            "Central Host Problem Details writer failure. TraceIdentifier: {TraceIdentifier}",
            CentralProblemDetailsWriter.GetTraceIdentifier(httpContext));
}
