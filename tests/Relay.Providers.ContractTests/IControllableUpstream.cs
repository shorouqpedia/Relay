namespace Relay.Providers.ContractTests;

/// <summary>
/// The ways an upstream can misbehave, as a closed set.
/// </summary>
/// <remarks>
/// Closed on purpose. These are the failure modes every provider has to handle,
/// so they are the failure modes every provider is tested against. A provider
/// with an interesting failure mode of its own tests it in its own suite; adding
/// it here would force the other providers to simulate something their upstream
/// cannot do.
/// </remarks>
public enum UpstreamBehaviour
{
    /// <summary>Respond normally.</summary>
    Accept,

    /// <summary>Fail with a server-side error that may not recur.</summary>
    ServerError,

    /// <summary>Refuse for quota reasons, reporting a retry delay where the protocol allows.</summary>
    RateLimit,

    /// <summary>Accept the connection and never answer.</summary>
    Hang,

    /// <summary>Answer with a body that does not parse.</summary>
    MalformedResponse,

    /// <summary>
    /// Fail with an error message containing the caller's own credential.
    /// </summary>
    /// <remarks>
    /// Not a hypothetical. Upstreams echo the request back in error payloads
    /// routinely, and a provider that copies the message into
    /// <c>FailureReason</c> puts the credential into the log store.
    /// </remarks>
    EchoCredentialsInError,
}

/// <summary>
/// A stand-in upstream whose behaviour a test can choose.
/// </summary>
/// <remarks>
/// Implemented once per provider, because each one talks to a different shape of
/// upstream — REST for most, an outbound webhook for one. The contract suite
/// drives it through this interface so the scenarios stay identical across
/// providers even though the transports do not.
/// </remarks>
public interface IControllableUpstream
{
    /// <summary>
    /// The credential the fake upstream echoes back for
    /// <see cref="UpstreamBehaviour.EchoCredentialsInError"/>.
    /// </summary>
    /// <remarks>
    /// A recognisable marker rather than a realistic-looking key, so that a
    /// failure of <c>PC11</c> is unambiguous and so that nothing in the repository
    /// resembles a real secret to a scanner.
    /// </remarks>
    public const string SecretMarker = "RELAY-CONTRACT-TEST-SECRET-MARKER";

    /// <summary>Sets how the upstream will respond to subsequent calls.</summary>
    void Behave(UpstreamBehaviour behaviour);
}
