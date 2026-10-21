using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

namespace Demo.DotnetCore.ConfigService.CentralHost;

public sealed class ManagementControllerDependencies
{
    internal StrictJsonRequestReader Reader { get; }

    internal IValidator<SetConfigurationRequest> SetValidator { get; }

    internal IValidator<RemoveConfigurationRequest> RemoveValidator { get; }

    internal IValidator<ConfigurationIdentityQuery> QueryValidator { get; }

    internal IValidator<EnvironmentQuery> EnvironmentQueryValidator { get; }

    internal ManagementModule Module { get; }

    internal ManagementControllerDependencies(
        StrictJsonRequestReader reader,
        IValidator<SetConfigurationRequest> setValidator,
        IValidator<RemoveConfigurationRequest> removeValidator,
        IValidator<ConfigurationIdentityQuery> queryValidator,
        IValidator<EnvironmentQuery> environmentQueryValidator,
        ManagementModule module)
    {
        Reader = reader;
        SetValidator = setValidator;
        RemoveValidator = removeValidator;
        QueryValidator = queryValidator;
        EnvironmentQueryValidator = environmentQueryValidator;
        Module = module;
    }
}

[ApiController]
[Route("api/v1/management/configurations")]
[SwaggerTag("管理介面：寫入、移除與查詢中央設定。給管理者與示範使用，Client 不會呼叫。")]
public sealed class ManagementConfigurationsController : ControllerBase
{
    private readonly ManagementControllerDependencies _dependencies;

    public ManagementConfigurationsController(ManagementControllerDependencies dependencies) =>
        _dependencies = dependencies;

    [HttpPost("set")]
    [JsonRequestBody(
        typeof(SetConfigurationRequest),
        """{ "environment": "UAT", "key": "Demo:Message", "value": "central-value" }""")]
    [SwaggerOperation(
        Summary = "寫入設定值（Set）",
        Description = "在中央環境寫入設定鍵的值，每次都附加一個新版本，即使值相同也一樣。`value` 必填，可為空字串，最多 16 KiB。回應不包含設定值。已經啟動的使用端不會收到新值，重新啟動後才會取得。")]
    [SwaggerResponse(StatusCodes.Status201Created, "已附加新版本，`state` 為 `Present`。", typeof(ConfigurationCommandResponse), OpenApiDescriptions.Json)]
    [SwaggerResponse(StatusCodes.Status400BadRequest, OpenApiDescriptions.ValidationFailed, typeof(ProblemDetails), OpenApiDescriptions.Problem)]
    [SwaggerResponse(StatusCodes.Status500InternalServerError, OpenApiDescriptions.ServerError, typeof(ProblemDetails), OpenApiDescriptions.Problem)]
    [SwaggerResponse(StatusCodes.Status503ServiceUnavailable, OpenApiDescriptions.Busy, typeof(ProblemDetails), OpenApiDescriptions.Problem)]
    public async Task<IActionResult> SetAsync(CancellationToken cancellationToken)
    {
        var request = await _dependencies.Reader.ReadRequiredAsync<SetConfigurationRequest>(
            Request,
            AllowedRequestMembers.Set,
            cancellationToken);
        await _dependencies.SetValidator.ValidateForContractAsync(request, cancellationToken);

        var receipt = await _dependencies.Module.SetAsync(
            EnvironmentName.FromValidated(request.Environment!),
            ConfigurationKey.FromValidated(request.Key!),
            request.Value!,
            cancellationToken);

        return StatusCode(
            StatusCodes.Status201Created,
            new ConfigurationCommandResponse(
                receipt.Environment.Value,
                receipt.Key.Value,
                receipt.Version,
                receipt.State,
                receipt.CreatedAtUtc));
    }

    [HttpPost("remove")]
    [JsonRequestBody(
        typeof(RemoveConfigurationRequest),
        """{ "environment": "UAT", "key": "Demo:Message" }""")]
    [SwaggerOperation(
        Summary = "移除設定值（Remove）",
        Description = "把設定鍵標示為已移除：附加一個值為 `null` 的新版本，歷史不會被刪除。請求不可帶 `value`。之後再 Set 同一個設定鍵可以恢復。")]
    [SwaggerResponse(StatusCodes.Status201Created, "已附加移除版本，`state` 為 `Removed`。", typeof(ConfigurationCommandResponse), OpenApiDescriptions.Json)]
    [SwaggerResponse(StatusCodes.Status400BadRequest, OpenApiDescriptions.ValidationFailed, typeof(ProblemDetails), OpenApiDescriptions.Problem)]
    [SwaggerResponse(StatusCodes.Status404NotFound, OpenApiDescriptions.NotFound, typeof(ProblemDetails), OpenApiDescriptions.Problem)]
    [SwaggerResponse(StatusCodes.Status409Conflict, OpenApiDescriptions.AlreadyRemoved, typeof(ProblemDetails), OpenApiDescriptions.Problem)]
    [SwaggerResponse(StatusCodes.Status500InternalServerError, OpenApiDescriptions.ServerError, typeof(ProblemDetails), OpenApiDescriptions.Problem)]
    [SwaggerResponse(StatusCodes.Status503ServiceUnavailable, OpenApiDescriptions.Busy, typeof(ProblemDetails), OpenApiDescriptions.Problem)]
    public async Task<IActionResult> RemoveAsync(CancellationToken cancellationToken)
    {
        var request = await _dependencies.Reader.ReadRequiredAsync<RemoveConfigurationRequest>(
            Request,
            AllowedRequestMembers.Remove,
            cancellationToken);
        await _dependencies.RemoveValidator.ValidateForContractAsync(request, cancellationToken);

        var receipt = await _dependencies.Module.RemoveAsync(
            EnvironmentName.FromValidated(request.Environment!),
            ConfigurationKey.FromValidated(request.Key!),
            cancellationToken);

        return StatusCode(
            StatusCodes.Status201Created,
            new ConfigurationCommandResponse(
                receipt.Environment.Value,
                receipt.Key.Value,
                receipt.Version,
                receipt.State,
                receipt.CreatedAtUtc));
    }

    [HttpGet]
    [SwaggerOperation(
        Summary = "列出中央環境的設定鍵",
        Description = "列出這個中央環境的每個設定鍵、最新版本、目前狀態與時間，包含已移除的設定鍵。不回傳設定值；要看值請用「查詢目前值」。沒有任何設定的環境回傳空陣列。")]
    [SwaggerResponse(StatusCodes.Status200OK, "依設定鍵排序的清單。", typeof(ConfigurationListResponse), OpenApiDescriptions.Json)]
    [SwaggerResponse(StatusCodes.Status400BadRequest, OpenApiDescriptions.ValidationFailed, typeof(ProblemDetails), OpenApiDescriptions.Problem)]
    [SwaggerResponse(StatusCodes.Status500InternalServerError, OpenApiDescriptions.ServerError, typeof(ProblemDetails), OpenApiDescriptions.Problem)]
    [SwaggerResponse(StatusCodes.Status503ServiceUnavailable, OpenApiDescriptions.Busy, typeof(ProblemDetails), OpenApiDescriptions.Problem)]
    public async Task<IActionResult> ListAsync(
        [FromQuery, SwaggerParameter(OpenApiDescriptions.EnvironmentParameter, Required = true)] string? environment,
        CancellationToken cancellationToken)
    {
        var query = new EnvironmentQuery(environment);
        await _dependencies.EnvironmentQueryValidator.ValidateForContractAsync(query, cancellationToken);

        var entries = await _dependencies.Module.ListAsync(
            EnvironmentName.FromValidated(query.Environment!),
            cancellationToken);
        var configurations = entries
            .Select(static entry => new ConfigurationSummaryResponse(
                entry.Key.Value,
                entry.Version,
                entry.State,
                entry.CreatedAtUtc))
            .ToArray();

        return Ok(new ConfigurationListResponse(query.Environment!, configurations));
    }

    [HttpGet("current")]
    [SwaggerOperation(
        Summary = "查詢目前值",
        Description = "回傳設定鍵的最新版本與目前值。已移除的設定鍵仍回傳 `200`，`state` 為 `Removed`、`value` 為 `null`。")]
    [SwaggerResponse(StatusCodes.Status200OK, "設定鍵的目前狀態與值。", typeof(CurrentConfigurationResponse), OpenApiDescriptions.Json)]
    [SwaggerResponse(StatusCodes.Status400BadRequest, OpenApiDescriptions.ValidationFailed, typeof(ProblemDetails), OpenApiDescriptions.Problem)]
    [SwaggerResponse(StatusCodes.Status404NotFound, OpenApiDescriptions.NotFound, typeof(ProblemDetails), OpenApiDescriptions.Problem)]
    [SwaggerResponse(StatusCodes.Status500InternalServerError, OpenApiDescriptions.ServerError, typeof(ProblemDetails), OpenApiDescriptions.Problem)]
    [SwaggerResponse(StatusCodes.Status503ServiceUnavailable, OpenApiDescriptions.Busy, typeof(ProblemDetails), OpenApiDescriptions.Problem)]
    public async Task<IActionResult> GetCurrentAsync(
        [FromQuery, SwaggerParameter(OpenApiDescriptions.EnvironmentParameter, Required = true)] string? environment,
        [FromQuery, SwaggerParameter(OpenApiDescriptions.KeyParameter, Required = true)] string? key,
        CancellationToken cancellationToken)
    {
        var query = new ConfigurationIdentityQuery(environment, key);
        await _dependencies.QueryValidator.ValidateForContractAsync(query, cancellationToken);

        var current = await _dependencies.Module.GetCurrentAsync(
            EnvironmentName.FromValidated(query.Environment!),
            ConfigurationKey.FromValidated(query.Key!),
            cancellationToken);

        return Ok(new CurrentConfigurationResponse(
            current.Environment.Value,
            current.Key.Value,
            current.Version,
            current.State,
            current.Value,
            current.CreatedAtUtc));
    }

    [HttpGet("history")]
    [SwaggerOperation(
        Summary = "查詢版本歷史",
        Description = "回傳設定鍵的所有版本，由新到舊排列，包含每次 Set 的值與 Remove 的紀錄。")]
    [SwaggerResponse(StatusCodes.Status200OK, "由新到舊的版本清單。", typeof(ConfigurationHistoryResponse), OpenApiDescriptions.Json)]
    [SwaggerResponse(StatusCodes.Status400BadRequest, OpenApiDescriptions.ValidationFailed, typeof(ProblemDetails), OpenApiDescriptions.Problem)]
    [SwaggerResponse(StatusCodes.Status404NotFound, OpenApiDescriptions.NotFound, typeof(ProblemDetails), OpenApiDescriptions.Problem)]
    [SwaggerResponse(StatusCodes.Status500InternalServerError, OpenApiDescriptions.ServerError, typeof(ProblemDetails), OpenApiDescriptions.Problem)]
    [SwaggerResponse(StatusCodes.Status503ServiceUnavailable, OpenApiDescriptions.Busy, typeof(ProblemDetails), OpenApiDescriptions.Problem)]
    public async Task<IActionResult> GetHistoryAsync(
        [FromQuery, SwaggerParameter(OpenApiDescriptions.EnvironmentParameter, Required = true)] string? environment,
        [FromQuery, SwaggerParameter(OpenApiDescriptions.KeyParameter, Required = true)] string? key,
        CancellationToken cancellationToken)
    {
        var query = new ConfigurationIdentityQuery(environment, key);
        await _dependencies.QueryValidator.ValidateForContractAsync(query, cancellationToken);

        var history = await _dependencies.Module.GetHistoryAsync(
            EnvironmentName.FromValidated(query.Environment!),
            ConfigurationKey.FromValidated(query.Key!),
            cancellationToken);
        var versions = history.Versions
            .Select(static version => new ConfigurationVersionResponse(
                version.Version,
                version.Operation,
                version.Value,
                version.CreatedAtUtc))
            .ToArray();

        return Ok(new ConfigurationHistoryResponse(
            history.Environment.Value,
            history.Key.Value,
            versions));
    }
}
