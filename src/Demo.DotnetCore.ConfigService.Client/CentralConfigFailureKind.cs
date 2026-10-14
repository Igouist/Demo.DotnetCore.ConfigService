namespace Demo.DotnetCore.ConfigService.Client;

public enum CentralConfigFailureKind
{
    InvalidBootstrapConfiguration,
    InvalidConfigurationPipeline,
    TransportFailure,
    AttemptTimeout,
    TotalTimeout,
    Unauthorized,
    HttpFailure,
    InvalidResponse,
    MissingRequiredKeys
}
