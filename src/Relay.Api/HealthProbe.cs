using System.Globalization;

namespace Relay.Api;

/// <summary>
/// Checks the running instance from inside its own container.
/// </summary>
/// <remarks>
/// Exists because the runtime image is chiseled: no shell, no curl, no wget, so a
/// Docker <c>HEALTHCHECK</c> has nothing to invoke but the application binary.
/// Running the app with <c>--healthcheck</c> is the documented pattern for
/// distroless containers.
/// <para>
/// It probes <c>/health/live</c>, not <c>/health/ready</c>. The container health
/// check answers "should this process be restarted?", and restarting a process
/// because its database is briefly unavailable turns a recoverable dependency
/// failure into a restart loop — every replica cycling while the one thing that
/// is actually broken stays broken.
/// </para>
/// </remarks>
internal static class HealthProbe
{
    /// <summary>Probes the local instance.</summary>
    /// <returns><c>0</c> when healthy, <c>1</c> otherwise — the exit codes Docker reads.</returns>
    public static async Task<int> RunAsync()
    {
        string port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? "8080";

        using var client = new HttpClient
        {
            // Short. A health check that waits ten seconds delays the restart
            // decision by ten seconds on every probe, and a process too slow to
            // answer a liveness probe promptly is not healthy anyway.
            Timeout = TimeSpan.FromSeconds(3),
        };

        try
        {
            using HttpResponseMessage response = await client
                .GetAsync(new Uri($"http://localhost:{port}/health/live", UriKind.Absolute))
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // Nothing is written to stdout on success and one line on failure.
            // Docker keeps health-check output, and a probe that logs on every
            // pass fills that buffer with nothing.
            await Console.Error.WriteLineAsync(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Health probe failed: {exception.Message}"))
                .ConfigureAwait(false);

            return 1;
        }
    }
}
