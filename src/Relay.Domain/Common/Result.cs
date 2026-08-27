using System.Diagnostics.CodeAnalysis;

namespace Relay.Domain.Common;

/// <summary>
/// The outcome of an operation that can fail in an expected way: either a value
/// or an <see cref="Error"/>, never both and never neither.
/// </summary>
/// <remarks>
/// See <c>docs/adr/0005-result-over-exceptions.md</c> for why this exists rather
/// than exceptions or a success-flag envelope.
/// <para>
/// <see cref="IsSuccess"/> is annotated so the compiler's nullable analysis knows
/// <see cref="Value"/> is non-null after a successful check. Without that, every
/// call site needs a null-forgiving operator, which would defeat the purpose.
/// </para>
/// </remarks>
/// <typeparam name="TValue">The type carried on success.</typeparam>
public readonly struct Result<TValue>
{
    private readonly TValue? _value;

    private Result(TValue value)
    {
        _value = value;
        Error = Error.None;
        IsSuccess = true;
    }

    private Result(Error error)
    {
        _value = default;
        Error = error;
        IsSuccess = false;
    }

    /// <summary>Whether the operation succeeded.</summary>
    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(true, nameof(_value))]
    public bool IsSuccess { get; }

    /// <summary>Whether the operation failed.</summary>
    [MemberNotNullWhen(false, nameof(Value))]
    public bool IsFailure => !IsSuccess;

    /// <summary>The failure. <see cref="Error.None"/> when successful.</summary>
    public Error Error { get; }

    /// <summary>
    /// The value produced on success.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The result is a failure. This is a programming error — the caller checked
    /// nothing — and so it throws rather than returning a default.
    /// </exception>
    public TValue Value => IsSuccess
        ? _value
        : throw new InvalidOperationException(
            $"Cannot read Value of a failed result. Error was '{Error.Code}'.");

    /// <summary>Creates a successful result.</summary>
    public static Result<TValue> Success(TValue value) => new(value);

    /// <summary>Creates a failed result.</summary>
    public static Result<TValue> Failure(Error error) => new(error);

    /// <summary>Implicitly lifts a value into a successful result.</summary>
    public static implicit operator Result<TValue>(TValue value) => new(value);

    /// <summary>Implicitly lifts an error into a failed result.</summary>
    public static implicit operator Result<TValue>(Error error) => new(error);

    /// <summary>
    /// Collapses the result into a single value by supplying a function for each case.
    /// </summary>
    /// <remarks>
    /// This is how a result should normally be consumed: it is impossible to read
    /// the value without having handled the failure, because both branches are
    /// required arguments.
    /// </remarks>
    public TOut Match<TOut>(Func<TValue, TOut> onSuccess, Func<Error, TOut> onFailure) =>
        IsSuccess ? onSuccess(_value) : onFailure(Error);

    /// <summary>Transforms the value, leaving a failure untouched.</summary>
    public Result<TOut> Map<TOut>(Func<TValue, TOut> map) =>
        IsSuccess ? Result<TOut>.Success(map(_value)) : Result<TOut>.Failure(Error);

    /// <summary>Chains another fallible operation, short-circuiting on failure.</summary>
    public Result<TOut> Bind<TOut>(Func<TValue, Result<TOut>> bind) =>
        IsSuccess ? bind(_value) : Result<TOut>.Failure(Error);
}

/// <summary>
/// The outcome of an operation that produces no value but can fail.
/// </summary>
public readonly struct Result
{
    private Result(Error error)
    {
        Error = error;
        IsSuccess = error == Error.None;
    }

    /// <summary>Whether the operation succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>Whether the operation failed.</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>The failure. <see cref="Error.None"/> when successful.</summary>
    public Error Error { get; }

    /// <summary>Creates a successful result.</summary>
    public static Result Success() => new(Error.None);

    /// <summary>Creates a failed result.</summary>
    public static Result Failure(Error error) => new(error);

    /// <summary>Implicitly lifts an error into a failed result.</summary>
    public static implicit operator Result(Error error) => new(error);

    /// <summary>Collapses the result into a single value by supplying a function for each case.</summary>
    public TOut Match<TOut>(Func<TOut> onSuccess, Func<Error, TOut> onFailure) =>
        IsSuccess ? onSuccess() : onFailure(Error);

    /// <summary>
    /// Returns the first failure among <paramref name="results"/>, or success if there is none.
    /// </summary>
    public static Result FirstFailure(params ReadOnlySpan<Result> results)
    {
        foreach (Result result in results)
        {
            if (result.IsFailure)
            {
                return result;
            }
        }

        return Success();
    }
}
