namespace Relay.Api;

/// <summary>
/// Names this assembly for <c>WebApplicationFactory</c>.
/// </summary>
/// <remarks>
/// A dedicated marker rather than the usual <c>public partial class Program</c>.
/// Every project using top-level statements generates a <c>Program</c>, and this
/// solution has three — the API, the worker, and the fake upstreams — so a test
/// project referencing more than one meets an ambiguous reference. A named type
/// says which assembly is meant and cannot collide.
/// <para>
/// It also lives in a namespace, which the generated <c>Program</c> cannot: top
/// level statements put it in the global one.
/// </para>
/// <para>
/// <c>WebApplicationFactory&lt;T&gt;</c> uses <c>T</c> only to locate the entry
/// assembly, so any public type in it will do.
/// </para>
/// </remarks>
public sealed class RelayApi;
