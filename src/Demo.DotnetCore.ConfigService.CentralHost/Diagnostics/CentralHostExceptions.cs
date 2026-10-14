namespace Demo.DotnetCore.ConfigService.CentralHost;

internal sealed class RequestValidationException : Exception
{
    public RequestValidationException(IReadOnlyList<ProblemDetailsError> errors)
        : base("One or more request fields are invalid.") => Errors = errors;

    public IReadOnlyList<ProblemDetailsError> Errors { get; }

    public static RequestValidationException Single(string path, string code, string message) =>
        new([new ProblemDetailsError(path, code, message)]);
}

internal enum PersistenceFailureCategory
{
    Locked,
    Other
}

internal sealed class PersistenceBusyException : Exception
{
    public PersistenceBusyException(Exception innerException)
        : base("The configuration store is busy.", innerException) { }
}

internal sealed class PersistenceFailureException : Exception
{
    public PersistenceFailureException(
        PersistenceFailureCategory category,
        Exception innerException)
        : base("The configuration store operation failed.", innerException) =>
        Category = category;

    public PersistenceFailureCategory Category { get; }
}

internal sealed class ConfigurationEntryNotFoundException : Exception
{
    public ConfigurationEntryNotFoundException()
        : base("The requested Configuration Entry was not found.") { }
}

internal sealed class ConfigurationEntryAlreadyRemovedException : Exception
{
    public ConfigurationEntryAlreadyRemovedException()
        : base("The requested Configuration Entry is already removed.") { }
}
