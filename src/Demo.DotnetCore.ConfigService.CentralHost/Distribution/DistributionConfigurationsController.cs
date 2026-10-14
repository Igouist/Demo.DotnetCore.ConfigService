using FluentValidation;
using Microsoft.AspNetCore.Mvc;

namespace Demo.DotnetCore.ConfigService.CentralHost;

public sealed class DistributionControllerDependencies
{
    internal StrictJsonRequestReader Reader { get; }

    internal IValidator<ResolveConfigurationsRequest> Validator { get; }

    internal DistributionModule Module { get; }

    internal DistributionControllerDependencies(
        StrictJsonRequestReader reader,
        IValidator<ResolveConfigurationsRequest> validator,
        DistributionModule module)
    {
        Reader = reader;
        Validator = validator;
        Module = module;
    }
}

[ApiController]
[Route("api/v1/distribution/configurations")]
public sealed class DistributionConfigurationsController : ControllerBase
{
    private readonly DistributionControllerDependencies _dependencies;

    public DistributionConfigurationsController(DistributionControllerDependencies dependencies) =>
        _dependencies = dependencies;

    [HttpPost("resolve")]
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

        return Ok(new ResolveConfigurationsResponse(resolution.Values, resolution.MissingKeys));
    }
}
