using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Relay.Domain.Common;

namespace Relay.Api;

/// <summary>
/// Turns an <see cref="Error"/> into an RFC 7807 response.
/// </summary>
/// <remarks>
/// The single place a domain failure becomes an HTTP status, and the reason
/// <see cref="Result{TValue}"/> is worth having (ADR 0005). Handlers never name a
/// status code; endpoints never interpret an error; this switch is the whole
/// translation.
/// <para>
/// It switches on <see cref="ErrorType"/> rather than on <see cref="Error.Code"/>,
/// so a new error code is mapped the moment it is created — categorised by the
/// person who knows what kind of failure it is, rather than by whoever notices it
/// returning 500 later.
/// </para>
/// </remarks>
internal static class ErrorResults
{
    /// <summary>Base for the <c>type</c> URI. Not dereferenced; it namespaces the codes.</summary>
    private const string TypeBase = "https://relay.example/problems/";

    /// <summary>Converts a failure into a problem response.</summary>
    public static ProblemHttpResult ToProblem(this Error error) =>
        TypedResults.Problem(
            title: TitleFor(error.Type),
            detail: error.Description,
            statusCode: StatusFor(error.Type),

            // The machine-readable half. `type` carries the stable code, so a
            // client branches on a URI that will not change rather than on a
            // status code shared by a dozen unrelated failures.
            type: TypeBase + error.Code,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = error.Code,
            });

    /// <summary>
    /// Maps an error category to a status code.
    /// </summary>
    /// <remarks>
    /// Exhaustive, with no default arm. Adding a member to
    /// <see cref="ErrorType"/> without deciding its status is a compile error —
    /// which is the point of categorising errors at all.
    /// </remarks>
    private static int StatusFor(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.Exhausted => StatusCodes.Status429TooManyRequests,

        // 503, not 500. The distinction matters to a caller: a dependency being
        // down is worth retrying, and a bug is not. Clients that retry on 5xx
        // indiscriminately are why the two need separate codes.
        ErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
        ErrorType.Failure => StatusCodes.Status500InternalServerError,
        _ => throw new ArgumentOutOfRangeException(
            nameof(type),
            type,
            "An error category reached the API boundary without a status mapping."),
    };

    private static string TitleFor(ErrorType type) => type switch
    {
        ErrorType.Validation => "The request is not valid.",
        ErrorType.NotFound => "The requested resource does not exist.",
        ErrorType.Conflict => "The request conflicts with the current state.",
        ErrorType.Forbidden => "The request is not permitted.",
        ErrorType.Exhausted => "A quota has been reached.",
        ErrorType.Unavailable => "A dependency is unavailable.",
        ErrorType.Failure => "The request could not be completed.",
        _ => "The request could not be completed.",
    };
}
