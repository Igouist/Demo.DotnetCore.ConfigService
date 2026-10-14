using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Demo.DotnetCore.ConfigService.CentralHost;

internal sealed record CentralProblemDescriptor(
    int Status,
    string Code,
    string Detail,
    IReadOnlyList<ProblemDetailsError>? Errors = null);

internal sealed class CentralProblemDetailsWriter
{
    private readonly IProblemDetailsService _problemDetailsService;

    public CentralProblemDetailsWriter(IProblemDetailsService problemDetailsService) =>
        _problemDetailsService = problemDetailsService;

    public async Task WriteAsync(
        HttpContext httpContext,
        CentralProblemDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var problemDetails = new ProblemDetails
        {
            Type = "about:blank",
            Title = ReasonPhrases.GetReasonPhrase(descriptor.Status),
            Status = descriptor.Status,
            Detail = descriptor.Detail,
            Instance = httpContext.Request.Path.Value
        };
        problemDetails.Extensions.Add("code", descriptor.Code);
        problemDetails.Extensions.Add("traceId", GetTraceIdentifier(httpContext));

        if (descriptor.Code == "validation_failed")
        {
            problemDetails.Extensions.Add("errors", descriptor.Errors ?? Array.Empty<ProblemDetailsError>());
        }

        httpContext.Response.StatusCode = descriptor.Status;
        await _problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails
        });
    }

    internal static string GetTraceIdentifier(HttpContext httpContext) =>
        Activity.Current?.Id ?? httpContext.TraceIdentifier;
}
