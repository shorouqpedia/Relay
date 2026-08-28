using System.Diagnostics;
using Relay.Providers.Abstractions;

namespace Relay.Providers.ContractTests;

/// <summary>
/// The two functions every callback verifier depends on.
/// </summary>
/// <remarks>
/// Both are short and both are easy to write in a way that looks correct, passes
/// every obvious test, and can be defeated. That is why they are shared code with
/// tests of their own rather than reimplemented per provider.
/// <para>Scenario ids <c>CS01</c>–<c>CS08</c> in <c>docs/test-plan.md</c>.</para>
/// </remarks>
public sealed class CallbackSignatureTests
{
    private const string Secret = "a-signing-secret-of-at-least-32-characters";

    private static readonly DateTimeOffset Now = new(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CS01_Matches_AcceptsASignatureOverTheSamePayload()
    {
        string signature = CallbackSignature.ComputeHex(Secret, "1743508800.{\"a\":1}");

        CallbackSignature.Matches(
            CallbackSignature.ComputeHex(Secret, "1743508800.{\"a\":1}"),
            signature).ShouldBeTrue();
    }

    [Fact]
    public void CS02_Matches_RejectsASignatureFromADifferentSecret()
    {
        string forged = CallbackSignature.ComputeHex("some-other-secret-of-sufficient-length", "payload");

        CallbackSignature.Matches(
            CallbackSignature.ComputeHex(Secret, "payload"),
            forged).ShouldBeFalse();
    }

    [Fact]
    public void CS03_Matches_RejectsASignatureOverADifferentPayload()
    {
        // The attack this defends against: capture a real callback, change the
        // message id to one you want marked delivered, keep the signature.
        CallbackSignature.Matches(
            CallbackSignature.ComputeHex(Secret, """{"message_id":"theirs"}"""),
            CallbackSignature.ComputeHex(Secret, """{"message_id":"mine"}""")).ShouldBeFalse();
    }

    [Fact]
    public void CS04_Matches_RejectsNullOnEitherSide()
    {
        CallbackSignature.Matches(null, "anything").ShouldBeFalse();
        CallbackSignature.Matches("anything", null).ShouldBeFalse();
        CallbackSignature.Matches(null, null).ShouldBeFalse();
    }

    [Fact]
    public void CS05_Matches_DoesNotReturnEarlyOnTheFirstDifferingByte()
    {
        string expected = CallbackSignature.ComputeHex(Secret, "payload");

        // Two wrong signatures: one differing in the first character, one only in
        // the last. A comparison that short-circuits takes measurably longer on
        // the second, and an attacker who can measure that recovers a valid
        // signature one character at a time — turning a 256-bit search into a few
        // hundred requests.
        string wrongAtStart = "0" + expected[1..];
        string wrongAtEnd = expected[..^1] + (expected[^1] == '0' ? '1' : '0');

        TimeSpan early = TimeOf(() => CallbackSignature.Matches(expected, wrongAtStart));
        TimeSpan late = TimeOf(() => CallbackSignature.Matches(expected, wrongAtEnd));

        CallbackSignature.Matches(expected, wrongAtStart).ShouldBeFalse();
        CallbackSignature.Matches(expected, wrongAtEnd).ShouldBeFalse();

        // A deliberately loose bound. Timing on a shared CI machine is noisy, so
        // asserting anything tight here would produce a test that fails for
        // reasons unrelated to the code. What it can catch is the difference
        // between constant time and a comparison that walks the string — which is
        // an order of magnitude, not a few percent.
        double ratio = late.TotalNanoseconds / Math.Max(early.TotalNanoseconds, 1);

        ratio.ShouldBeLessThan(
            10,
            $"Comparison took {early.TotalNanoseconds:0}ns for an early difference and "
            + $"{late.TotalNanoseconds:0}ns for a late one, which suggests it short-circuits.");
    }

    [Fact]
    public void CS06_IsWithinTolerance_AcceptsARecentTimestamp()
    {
        string recent = Now.AddSeconds(-30).ToUnixTimeSeconds().ToString(
            System.Globalization.CultureInfo.InvariantCulture);

        CallbackSignature.IsWithinTolerance(recent, Now, TimeSpan.FromMinutes(5)).ShouldBeTrue();
    }

    [Fact]
    public void CS07_IsWithinTolerance_RejectsAReplayedTimestamp()
    {
        string old = Now.AddHours(-2).ToUnixTimeSeconds().ToString(
            System.Globalization.CultureInfo.InvariantCulture);

        // Without this check a captured callback stays valid forever, and one
        // observed request can be replayed indefinitely with every copy verifying.
        CallbackSignature.IsWithinTolerance(old, Now, TimeSpan.FromMinutes(5)).ShouldBeFalse();
    }

    [Fact]
    public void CS08_IsWithinTolerance_AcceptsAClockThatRunsSlightlyFast()
    {
        string future = Now.AddSeconds(30).ToUnixTimeSeconds().ToString(
            System.Globalization.CultureInfo.InvariantCulture);

        // Checked in both directions. A provider whose clock is a little ahead
        // sends timestamps in the future, and rejecting those would fail
        // legitimate traffic for a reason nobody would think to look for.
        CallbackSignature.IsWithinTolerance(future, Now, TimeSpan.FromMinutes(5)).ShouldBeTrue();

        string wayAhead = Now.AddHours(2).ToUnixTimeSeconds().ToString(
            System.Globalization.CultureInfo.InvariantCulture);

        CallbackSignature.IsWithinTolerance(wayAhead, Now, TimeSpan.FromMinutes(5)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("2026-04-01T12:00:00Z")]
    public void CS09_IsWithinTolerance_RejectsATimestampItCannotRead(string? value)
    {
        // An unreadable timestamp is a rejection, not an exception and not a pass.
        // Treating it as absent-and-therefore-fine would remove the replay bound
        // for any caller who simply omits the field.
        CallbackSignature.IsWithinTolerance(value, Now, TimeSpan.FromMinutes(5)).ShouldBeFalse();
    }

    private static TimeSpan TimeOf(Func<bool> action)
    {
        // Warmed and averaged. A single measurement of something this fast is
        // dominated by JIT and scheduling.
        for (int i = 0; i < 1_000; i++)
        {
            action();
        }

        long start = Stopwatch.GetTimestamp();

        for (int i = 0; i < 20_000; i++)
        {
            action();
        }

        return Stopwatch.GetElapsedTime(start) / 20_000;
    }
}
