using FluentValidation;
using FluentValidation.Results;

namespace Relay.Api;

/// <summary>
/// Validates an endpoint's argument before the endpoint runs.
/// </summary>
/// <remarks>
/// The endpoint-filter equivalent of a mediator pipeline behavior, and the reason
/// this project does not need one (ADR 0012). Attached to a
/// <c>MapGroup</c>, it runs before every endpoint in that group, and — unlike a
/// behavior registered elsewhere — it is visible in the group declaration
/// directly above the endpoints it guards.
/// <para>
/// Registered per request type rather than generically over the first argument,
/// because "the first argument" is a fragile thing to depend on: adding a route
/// parameter silently changes which value gets validated, and the failure is a
/// validator that quietly stops running.
/// </para>
/// </remarks>
/// <typeparam name="T">The argument type to validate.</typeparam>
internal sealed class ValidationFilter<T>(IValidator<T> validator) : IEndpointFilter
    where T : class
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        T? argument = context.Arguments.OfType<T>().FirstOrDefault();

        if (argument is null)
        {
            // The endpoint does not take what this filter validates. A wiring
            // mistake, and one worth failing loudly for: the alternative is an
            // endpoint that looks validated and is not.
            throw new InvalidOperationException(
                $"ValidationFilter<{typeof(T).Name}> is attached to an endpoint that does "
                + $"not take a {typeof(T).Name}. Nothing would be validated.");
        }

        ValidationResult result = await validator
            .ValidateAsync(argument, context.HttpContext.RequestAborted)
            .ConfigureAwait(false);

        if (result.IsValid)
        {
            return await next(context).ConfigureAwait(false);
        }

        // Every failure at once, in the standard RFC 7807 shape for validation.
        // Reporting only the first would make a caller with four bad fields spend
        // four round trips finding that out.
        return TypedResults.ValidationProblem(
            result.ToDictionary(),
            title: "The request is not valid.");
    }
}
