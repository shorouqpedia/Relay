using System.ComponentModel.DataAnnotations;

namespace Relay.Providers.Email.Mailhook;

/// <summary>
/// Settings for the Mailhook email provider, bound from <c>Providers:email.mailhook</c>.
/// </summary>
public sealed class MailhookOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Providers:email.mailhook";

    /// <summary>Base address of the Mailhook API.</summary>
    [Required]
    [Url]
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// API key, sent in a vendor-specific header.
    /// </summary>
    /// <remarks>
    /// Mailhook uses <c>X-Api-Key</c> rather than a bearer token. That difference
    /// stops here: nothing outside this assembly knows how Mailhook authenticates.
    /// </remarks>
    [Required]
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>Sender address Mailhook will send from.</summary>
    [Required]
    [EmailAddress]
    public string FromAddress { get; init; } = string.Empty;

    /// <summary>
    /// The secret Mailhook signs its delivery callbacks with.
    /// </summary>
    /// <remarks>
    /// Distinct from the API key. One authenticates Relay to Mailhook on the way out,
    /// the other authenticates Mailhook to Relay on the way back — sharing a value would
    /// mean an outbound credential leak also lets an attacker forge receipts.
    /// </remarks>
    [Required]
    [MinLength(32)]
    public string CallbackSecret { get; init; } = string.Empty;

    /// <summary>How long to wait for a response before treating the silence as a timeout.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:02:00")]
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}
