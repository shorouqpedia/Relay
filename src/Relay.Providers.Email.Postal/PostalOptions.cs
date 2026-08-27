using System.ComponentModel.DataAnnotations;

namespace Relay.Providers.Email.Postal;

/// <summary>
/// Settings for the Postal email provider, bound from <c>Providers:email.postal</c>.
/// </summary>
/// <remarks>
/// Validated at startup rather than on first use. A misconfigured provider that
/// fails on its first delivery attempt has already cost a message; one that fails
/// at startup costs a deployment, which is the cheaper place to find out.
/// </remarks>
public sealed class PostalOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Providers:email.postal";

    /// <summary>Base address of the Postal API.</summary>
    [Required]
    [Url]
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// API key, sent as a bearer token.
    /// </summary>
    /// <remarks>
    /// Supplied through user secrets in development and the environment in
    /// deployment. It is never written to a checked-in configuration file — CI
    /// asserts that every secret-bearing key ships without a value.
    /// </remarks>
    [Required]
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>Sender address Postal will send from.</summary>
    [Required]
    [EmailAddress]
    public string FromAddress { get; init; } = string.Empty;

    /// <summary>
    /// How long to wait for a response before treating the silence as a timeout.
    /// </summary>
    /// <remarks>
    /// Short on purpose. A slow provider call holds a worker slot, so a generous
    /// timeout under load turns one degraded provider into a stalled pipeline. Ten
    /// seconds is well past Postal's normal response time and well short of the
    /// point where waiting is cheaper than retrying.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:01", "00:02:00")]
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}
