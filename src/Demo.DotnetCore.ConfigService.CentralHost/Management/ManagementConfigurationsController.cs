using FluentValidation;
using Microsoft.AspNetCore.Mvc;

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
public sealed class ManagementConfigurationsController : ControllerBase
{
    private readonly ManagementControllerDependencies _dependencies;

    public ManagementConfigurationsController(ManagementControllerDependencies dependencies) =>
        _dependencies = dependencies;

    [HttpPost("set")]
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
    public async Task<IActionResult> ListAsync(
        [FromQuery] string? environment,
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
    public async Task<IActionResult> GetCurrentAsync(
        [FromQuery] string? environment,
        [FromQuery] string? key,
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
    public async Task<IActionResult> GetHistoryAsync(
        [FromQuery] string? environment,
        [FromQuery] string? key,
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
