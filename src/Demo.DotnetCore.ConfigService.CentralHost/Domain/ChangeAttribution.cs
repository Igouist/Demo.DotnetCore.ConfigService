namespace Demo.DotnetCore.ConfigService.CentralHost;

// POC-063: who changed an entry and why. ChangedBy is self-reported by the caller today; a future sign-in
// can build this from the authenticated identity instead without changing the stored shape.
internal sealed record ChangeAttribution
{
    public const int MaxChangedByLength = 100;
    public const int MaxReasonLength = 500;

    private ChangeAttribution(string changedBy, string? reason)
    {
        ChangedBy = changedBy;
        Reason = reason;
    }

    public string ChangedBy { get; }

    public string? Reason { get; }

    public static bool IsValidChangedBy(string? candidate) =>
        Normalize(candidate) is { } normalized && IsWithin(normalized, MaxChangedByLength);

    public static bool IsValidReason(string? candidate) =>
        Normalize(candidate) is not { } normalized || IsWithin(normalized, MaxReasonLength);

    public static bool HasNoControlCharacters(string? candidate) =>
        candidate is null || !candidate.Any(char.IsControl);

    public static ChangeAttribution FromValidated(string? changedBy, string? reason) =>
        IsValidChangedBy(changedBy) && HasNoControlCharacters(changedBy)
            && IsValidReason(reason) && HasNoControlCharacters(reason)
            ? new ChangeAttribution(Normalize(changedBy)!, Normalize(reason))
            : throw new ArgumentException("The change attribution must be validated before construction.");

    // Restores a stored attribution; a missing ChangedBy is a corrupt row, not a validation error.
    public static ChangeAttribution? FromStored(string? changedBy, string? reason) =>
        string.IsNullOrEmpty(changedBy) ? null : new ChangeAttribution(changedBy, reason);

    private static string? Normalize(string? candidate) =>
        string.IsNullOrWhiteSpace(candidate) ? null : candidate.Trim();

    private static bool IsWithin(string value, int maxLength) => value.Length <= maxLength;
}
