using Bitstream.Application.Services.Activation;
using Bitstream.Domain.Entities;

namespace Bitstream.Web.Presentation;

/// <summary>The activation request View drawer: the request, its timeline, and whether the caller may see raw payloads.</summary>
/// <param name="Request">The request, already ownership-checked.</param>
/// <param name="Timeline">Every CRM message and portal user action, oldest first.</param>
/// <param name="ShowRawData">True for <c>activation.read.all</c> (Administrator, Auditor, Service Desk).</param>
public sealed record ActivationRequestDetailsViewModel(
    ActivationRequest Request,
    IReadOnlyList<ActivationTimelineEntry> Timeline,
    bool ShowRawData);
