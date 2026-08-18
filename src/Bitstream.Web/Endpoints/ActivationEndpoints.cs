using Bitstream.Application.Services;
using Bitstream.Application.Services.Activation;
using Bitstream.Domain.Entities;
using Bitstream.Hosting.Configuration;
using Bitstream.Web.Contracts;
using Bitstream.Web.Security;
using Microsoft.AspNetCore.Mvc;

namespace Bitstream.Web.Endpoints;

/// <summary>
/// TRD 5: activation request submission and the GIS verification admin screen.
/// <para>
/// Submission and the read endpoint are open to any authenticated caller at the route level;
/// <see cref="IActivationRequestService"/> enforces ownership from identity, before the
/// repository is touched, the same way <see cref="AdministrationEndpoints"/> does for ISPs and
/// users (TR-SEC-18, TR-SEC-19). Recording a GIS outcome is Administrator-only (TR-ACT-12).
/// </para>
/// </summary>
public static class ActivationEndpoints
{
    public static IEndpointRouteBuilder MapActivationEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/activation-requests")
            .WithTags("Activation requests")
            .RequireRateLimiting(RateLimitPolicies.Administration);

        group.MapPost("/", SubmitAsync)
            .WithName("SubmitActivationRequest")
            .WithSummary("Submit an activation request")
            .WithDescription(
                "TR-ACT-01 to TR-ACT-06: package, location and classification are validated " +
                "server-side; the location is parsed into normalised coordinates. The public " +
                "identifier is issued and the record persisted with status Submitted before any " +
                "CRM call is even enqueued (TR-DAT-01).")
            .Accepts<SubmitActivationHttpRequest>("application/json")
            .Produces<ActivationRequestResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequirePermission(ActivationPermissionCodes.ActivationCreate);

        group.MapGet("/{publicId}", GetAsync)
            .WithName("GetActivationRequest")
            .WithSummary("Read an activation request")
            .WithDescription(
                "An ISP user may read their own ISP's requests; activation.read.all reads any " +
                "ISP's. A request for another ISP's returns 404, identically to one that does " +
                "not exist (TR-SEC-19).")
            .Produces<ActivationRequestResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization();

        group.MapPatch("/{requestId:long}/gis-outcome", RecordGisOutcomeAsync)
            .WithName("RecordGisOutcome")
            .WithSummary("Record the manual GIS verification outcome")
            .WithDescription(
                "TR-ACT-12 to TR-ACT-19: the GIS verification admin screen. lineAvailable true " +
                "moves the request to LineAvailable; false moves it to RejectedNoLine and " +
                "requires a reason (TR-ACT-13). Only permitted from AwaitingGisVerification " +
                "(TRD 5.3); any other current status is a 409.")
            .Accepts<GisOutcomeRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(ActivationPermissionCodes.ActivationGisRecord);

        return app;
    }

    private static async Task<IResult> SubmitAsync(
        [FromBody] SubmitActivationHttpRequest request,
        IActivationRequestService activationRequestService,
        CancellationToken cancellationToken)
    {
        try
        {
            var activationRequest = await activationRequestService.SubmitAsync(
                new SubmitActivationRequest(
                    request.IspId, request.PackageCode, request.LocationRaw,
                    request.Classification ?? string.Empty, request.ContractDurationMonths, request.Comments),
                cancellationToken).ConfigureAwait(false);

            return Results.CreatedAtRoute(
                "GetActivationRequest", new { publicId = activationRequest.PublicId }, ToResponse(activationRequest));
        }
        catch (ActivationRequestValidationException exception)
        {
            return ValidationProblem(exception);
        }
    }

    private static async Task<IResult> GetAsync(
        [FromRoute] string publicId,
        IActivationRequestService activationRequestService,
        CancellationToken cancellationToken)
    {
        var activationRequest = await activationRequestService.GetByPublicIdAsync(publicId, cancellationToken).ConfigureAwait(false);

        // Not found and forbidden are the same response on purpose (TR-SEC-19).
        return activationRequest is null ? Results.NotFound() : Results.Ok(ToResponse(activationRequest));
    }

    private static async Task<IResult> RecordGisOutcomeAsync(
        [FromRoute] long requestId,
        [FromBody] GisOutcomeRequest request,
        IActivationRequestService activationRequestService,
        CancellationToken cancellationToken)
    {
        try
        {
            await activationRequestService.RecordGisOutcomeAsync(requestId, request.LineAvailable, request.Reason, cancellationToken)
                .ConfigureAwait(false);

            return Results.NoContent();
        }
        catch (ActivationRequestNotFoundException)
        {
            return Results.NotFound();
        }
        catch (ActivationRequestValidationException exception)
        {
            return ValidationProblem(exception);
        }
        catch (ActivationRequestConflictException exception)
        {
            return Results.Problem(
                title: "Invalid state transition",
                detail: exception.Message,
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static IResult ValidationProblem(ActivationRequestValidationException exception) =>
        Results.ValidationProblem(
            exception.Violations.Count > 0
                ? new Dictionary<string, string[]> { ["request"] = [.. exception.Violations] }
                : new Dictionary<string, string[]> { ["request"] = [exception.Message] });

    private static ActivationRequestResponse ToResponse(ActivationRequest request) =>
        new(request.RequestId, request.PublicId, request.IspId, request.PackageCode, request.LocationRaw,
            request.LocationLat, request.LocationLng, request.Classification, request.ContractDurationMonths,
            request.Comments, request.Status.ToString(), request.StatusReason, request.SalesOrderId,
            request.CreatedAt, request.LastUpdatedAt);
}
