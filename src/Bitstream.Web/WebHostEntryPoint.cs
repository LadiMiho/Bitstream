namespace Bitstream.Web;

/// <summary>
/// Entry-point marker for this host, so a test can bootstrap the real pipeline with
/// <c>WebApplicationFactory&lt;WebHostEntryPoint&gt;</c> rather than a re-declared approximation.
/// <para>
/// A named marker rather than <c>public partial class Program</c>, for the same reason
/// <c>ApiHostEntryPoint</c> is one: top-level statements generate a <c>Program</c> in the global
/// namespace of every host, so one test project referencing both hosts sees two of them. Making
/// either public is enough to collide, because the SDK already grants
/// <c>Bitstream.Api.Tests</c> access to <c>Bitstream.Api</c>'s internals — the test project's
/// name matches the convention exactly. Both hosts therefore keep their generated
/// <c>Program</c> internal and expose a distinct marker instead.
/// </para>
/// <para>
/// <c>WebApplicationFactory</c> uses the type only to locate its assembly, so any public type
/// in it serves.
/// </para>
/// </summary>
public sealed class WebHostEntryPoint;
