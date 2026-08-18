namespace Bitstream.Api;

/// <summary>
/// Entry-point marker for this host, so a test can bootstrap the real pipeline with
/// <c>WebApplicationFactory&lt;ApiHostEntryPoint&gt;</c> rather than a re-declared approximation.
/// <para>
/// A named marker rather than <c>public partial class Program</c>, which is what the portal host
/// uses: top-level statements generate a <c>Program</c> in the global namespace of every host, so
/// making both public would collide in any test project that references both. Leaving this
/// host's <c>Program</c> internal and pointing the factory at a distinct type keeps one test
/// project able to exercise both hosts — which the CRM round trip needs, since a request is
/// submitted on the portal and dispatched by the API. <c>WebApplicationFactory</c> uses the type
/// only to locate its assembly, so any public type in it serves.
/// </para>
/// </summary>
public sealed class ApiHostEntryPoint;
