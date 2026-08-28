using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Relay.Providers.Abstractions;

/// <summary>
/// The signature checks every callback verifier needs.
/// </summary>
/// <remarks>
/// Shared because the mechanics are identical across providers even though the
/// details are not: they differ in which header carries the signature, how it is
/// encoded, and what string is signed — not in how a MAC is compared or how a
/// clock skew window works.
/// <para>
/// This is the one piece of callback handling that does belong in shared code.
/// Getting either of these two functions subtly wrong produces a system that
/// verifies signatures in a way that looks correct, passes every test, and can be
/// defeated.
/// </para>
/// </remarks>
public static class CallbackSignature
{
    /// <summary>
    /// How far a callback's timestamp may be from now.
    /// </summary>
    /// <remarks>
    /// A signature over a body alone is valid forever, so a captured callback can
    /// be replayed indefinitely. Including a timestamp in the signed string and
    /// bounding it is what closes that.
    /// <para>
    /// Five minutes is a trade rather than a magic number. Tighter, and a
    /// provider's own retry after a network delay is rejected as an attack; looser,
    /// and a captured callback stays useful for longer. It also has to absorb clock
    /// skew between Relay and a provider, which is why the window is applied in
    /// both directions.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    /// <summary>Computes an HMAC-SHA256 over <paramref name="payload"/>, lowercase hex.</summary>
    public static string ComputeHex(string secret, string payload) =>
        Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload)));

    /// <summary>Computes an HMAC-SHA256 over <paramref name="payload"/>, base64.</summary>
    /// <remarks>Providers disagree about encoding; both are here so neither has to reimplement it.</remarks>
    public static string ComputeBase64(string secret, string payload) =>
        Convert.ToBase64String(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload)));

    /// <summary>
    /// Compares two signatures without leaking how much of one was right.
    /// </summary>
    /// <remarks>
    /// The reason this exists rather than <c>==</c>.
    /// <para>
    /// String equality returns as soon as it finds a differing character, so the
    /// time a rejection takes reveals how many leading characters were correct. An
    /// attacker who can measure that recovers a valid signature one character at a
    /// time, which turns a 256-bit search into a few hundred requests.
    /// <see cref="CryptographicOperations.FixedTimeEquals"/> always examines every
    /// byte.
    /// </para>
    /// <para>
    /// Length is compared first and returns early, which is safe: the length of a
    /// signature is not secret, and it is fixed by the algorithm anyway.
    /// </para>
    /// </remarks>
    public static bool Matches(string? expected, string? actual)
    {
        if (expected is null || actual is null)
        {
            return false;
        }

        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        byte[] actualBytes = Encoding.UTF8.GetBytes(actual);

        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    /// <summary>
    /// Whether a Unix-second timestamp is close enough to now.
    /// </summary>
    /// <remarks>
    /// Both directions. A callback from a provider whose clock runs slightly fast
    /// arrives with a timestamp in the future, and rejecting those would fail
    /// legitimate traffic for a reason nobody would think to look for.
    /// </remarks>
    public static bool IsWithinTolerance(string? unixSeconds, DateTimeOffset now, TimeSpan tolerance)
    {
        if (!long.TryParse(unixSeconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
        {
            return false;
        }

        TimeSpan drift = now - DateTimeOffset.FromUnixTimeSeconds(value);

        return drift.Duration() <= tolerance;
    }
}
