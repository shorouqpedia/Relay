namespace Relay.Domain.Common;

/// <summary>
/// The category of an expected failure. Determines how the boundary maps it —
/// to an HTTP status, to a retry decision, to a log level — without the boundary
/// needing to know every individual error code.
/// </summary>
public enum ErrorType
{
    /// <summary>The request was malformed or violated a rule that the caller can fix.</summary>
    Validation,

    /// <summary>The referenced thing does not exist.</summary>
    NotFound,

    /// <summary>The request conflicts with the current state.</summary>
    Conflict,

    /// <summary>The caller is not permitted to do this.</summary>
    Forbidden,

    /// <summary>A quota or rate limit was reached.</summary>
    Exhausted,

    /// <summary>A dependency is unavailable. Distinct from Failure because it is retryable.</summary>
    Unavailable,

    /// <summary>The operation failed for a reason the caller cannot act on.</summary>
    Failure,
}

/// <summary>
/// An expected failure, as a value.
/// </summary>
/// <remarks>
/// <see cref="Code"/> is a stable, machine-readable token (<c>message.not_found</c>)
/// that callers may branch on. <see cref="Description"/> is for humans and may change
/// freely; nothing should parse it.
/// </remarks>
public sealed record Error
{
    /// <summary>The absence of an error. Returned by successful results that carry no value.</summary>
    public static readonly Error None = new(string.Empty, ErrorType.Failure, string.Empty);

    private Error(string code, ErrorType type, string description)
    {
        Code = code;
        Type = type;
        Description = description;
    }

    /// <summary>Stable, machine-readable identifier for this failure.</summary>
    public string Code { get; }

    /// <summary>The category, used to map the failure at the boundary.</summary>
    public ErrorType Type { get; }

    /// <summary>Human-readable explanation. Not part of the contract.</summary>
    public string Description { get; }

    /// <summary>Creates a <see cref="ErrorType.Validation"/> error.</summary>
    public static Error Validation(string code, string description) =>
        new(code, ErrorType.Validation, description);

    /// <summary>Creates a <see cref="ErrorType.NotFound"/> error.</summary>
    public static Error NotFound(string code, string description) =>
        new(code, ErrorType.NotFound, description);

    /// <summary>Creates a <see cref="ErrorType.Conflict"/> error.</summary>
    public static Error Conflict(string code, string description) =>
        new(code, ErrorType.Conflict, description);

    /// <summary>Creates a <see cref="ErrorType.Forbidden"/> error.</summary>
    public static Error Forbidden(string code, string description) =>
        new(code, ErrorType.Forbidden, description);

    /// <summary>Creates an <see cref="ErrorType.Exhausted"/> error.</summary>
    public static Error Exhausted(string code, string description) =>
        new(code, ErrorType.Exhausted, description);

    /// <summary>Creates an <see cref="ErrorType.Unavailable"/> error.</summary>
    public static Error Unavailable(string code, string description) =>
        new(code, ErrorType.Unavailable, description);

    /// <summary>Creates a <see cref="ErrorType.Failure"/> error.</summary>
    public static Error Failure(string code, string description) =>
        new(code, ErrorType.Failure, description);

    /// <inheritdoc />
    public override string ToString() => $"{Code}: {Description}";
}
