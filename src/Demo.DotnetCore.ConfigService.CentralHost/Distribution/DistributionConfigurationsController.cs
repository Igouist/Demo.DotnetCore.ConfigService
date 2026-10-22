using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Swashbuckle.AspNetCore.Annotations;

namespace Demo.DotnetCore.ConfigService.CentralHost;

public sealed class DistributionControllerDependencies
{
    internal StrictJsonRequestReader Reader { get; }

    internal IValidator<ResolveConfigurationsRequest> Validator { get; }

    internal DistributionModule Module { get; }

    internal ILogger Logger { get; }

    internal DistributionControllerDependencies(
        StrictJsonRequestReader reader,
        IValidator<ResolveConfigurationsRequest> validator,
        DistributionModule module,
        ILogger<DistributionConfigurationsController> logger)
    {
        Reader = reader;
        Validator = validator;
        Module = module;
        Logger = logger;
    }
}

[ApiController]
[Route("api/v1/distribution/configurations")]
[SwaggerTag("分發介面：Client 在使用端啟動時呼叫，一次取得需要的設定值。")]
public sealed class DistributionConfigurationsController : ControllerBase
{
    private readonly DistributionControllerDependencies _dependencies;

    public DistributionConfigurationsController(DistributionControllerDependencies dependencies) =>
        _dependencies = dependencies;

    [HttpPost("resolve")]
    [JsonRequestBody(
        typeof(ResolveConfigurationsRequest),
        """{ "environment": "UAT", "keys": ["Demo:Message", "Demo:LocalFallback"] }""")]
    [SwaggerOperation(
        Summary = "取得多個設定值（Resolve）",
        Description = "Client 啟動時呼叫的介面。傳入中央環境與需要的設定鍵，存在的放在 `values`，從未建立或已移除的放在 `missingKeys`。缺值不會讓請求失敗；是否阻止啟動由 Client 依設定鍵是否必要來決定。`revision` 是依這次請求的設定鍵與各自版本號計算的指紋，只能比較相同或不同。")]
    [SwaggerResponse(StatusCodes.Status200OK, "`values` 與 `missingKeys` 剛好涵蓋請求的每個設定鍵。", typeof(ResolveConfigurationsResponse), OpenApiDescriptions.Json)]
    [SwaggerResponse(StatusCodes.Status400BadRequest, OpenApiDescriptions.ValidationFailed, typeof(ProblemDetails), OpenApiDescriptions.Problem)]
    [SwaggerResponse(StatusCodes.Status500InternalServerError, OpenApiDescriptions.ServerError, typeof(ProblemDetails), OpenApiDescriptions.Problem)]
    [SwaggerResponse(StatusCodes.Status503ServiceUnavailable, OpenApiDescriptions.Busy, typeof(ProblemDetails), OpenApiDescriptions.Problem)]
    public async Task<IActionResult> ResolveAsync(CancellationToken cancellationToken)
    {
        var request = await _dependencies.Reader.ReadRequiredAsync<ResolveConfigurationsRequest>(
            Request,
            AllowedRequestMembers.Resolve,
            cancellationToken);
        await _dependencies.Validator.ValidateForContractAsync(request, cancellationToken);

        var keys = request.Keys!
            .Select(static key => ConfigurationKey.FromValidated(key!))
            .ToArray();
        var resolution = await _dependencies.Module.ResolveAsync(
            EnvironmentName.FromValidated(request.Environment!),
            keys,
            cancellationToken);

        // POC-062: the revision is one-way, so record the per-key versions behind it for later lookup in history.
        _dependencies.Logger.LogInformation(
            "Central Host resolved configuration. TraceIdentifier: {TraceIdentifier}; Environment: {Environment}; Revision: {Revision}; KeyVersions: {KeyVersions}",
            CentralProblemDetailsWriter.GetTraceIdentifier(HttpContext),
            request.Environment,
            resolution.Revision,
            resolution.KeyVersions.Select(static keyVersion => $"{keyVersion.Key}={keyVersion.Version}").ToArray());

        return Ok(new ResolveConfigurationsResponse(resolution.Values, resolution.MissingKeys, resolution.Revision));
    }
}
