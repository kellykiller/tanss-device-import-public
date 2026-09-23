using Fido2NetLib;

namespace TnsApiImport;

internal sealed record SecurityKeyRegistrationOptionsRequest(
    string? EnrollmentCode,
    string? Label);

internal sealed record SecurityKeyRegistrationCompleteRequest(
    string? TransactionId,
    AuthenticatorAttestationRawResponse? Response);

internal sealed record SecurityKeyAssertionCompleteRequest(
    string? TransactionId,
    AuthenticatorAssertionRawResponse? Response);

internal static class SecurityKeyModule
{
    public const string RegistrationOptionsPath =
        "/api/v1/auth/security-key/registration/options";
    public const string RegistrationCompletePath =
        "/api/v1/auth/security-key/registration/complete";
    public const string SessionOptionsPath =
        "/api/v1/auth/security-key/session/options";
    public const string SessionCompletePath =
        "/api/v1/auth/security-key/session/complete";

    public static bool IsAnonymousAuthenticationPath(PathString path) =>
        path.Equals(RegistrationOptionsPath) ||
        path.Equals(RegistrationCompletePath) ||
        path.Equals(SessionOptionsPath) ||
        path.Equals(SessionCompletePath);

    public static void MapSecurityKeyAuthentication(this WebApplication app)
    {
        app.MapPost(
                RegistrationOptionsPath,
                (SecurityKeyRegistrationOptionsRequest request,
                 HttpRequest httpRequest,
                 HttpResponse httpResponse,
                 SecurityKeyAuthenticationService service) =>
                {
                    SetNoStore(httpResponse);

                    if (!httpRequest.IsHttps)
                    {
                        return HttpsRequired();
                    }

                    try
                    {
                        var result = service.CreateRegistrationOptions(
                            request.EnrollmentCode,
                            request.Label);

                        return Results.Ok(new
                        {
                            status = "registration-required",
                            transactionId = result.TransactionId,
                            options = result.Options
                        });
                    }
                    catch (SecurityKeyProtocolException exception)
                    {
                        return ToProblem(exception);
                    }
                })
            .RequireRateLimiting("security-key-enrollment");

        app.MapPost(
                RegistrationCompletePath,
                async Task<IResult> (
                    SecurityKeyRegistrationCompleteRequest request,
                    HttpRequest httpRequest,
                    HttpResponse httpResponse,
                    SecurityKeyAuthenticationService service,
                    CancellationToken cancellationToken) =>
                {
                    SetNoStore(httpResponse);

                    if (!httpRequest.IsHttps)
                    {
                        return HttpsRequired();
                    }

                    try
                    {
                        var result = await service.CompleteRegistrationAsync(
                            request.TransactionId,
                            request.Response,
                            cancellationToken);

                        return Results.Ok(new
                        {
                            status = "registered",
                            authenticationMethod = "FIDO2",
                            keyLabel = result.Label,
                            registeredUtc = result.RegisteredUtc
                        });
                    }
                    catch (SecurityKeyProtocolException exception)
                    {
                        return ToProblem(exception);
                    }
                })
            .RequireRateLimiting("security-key-enrollment");

        app.MapPost(
                SessionOptionsPath,
                (HttpRequest httpRequest,
                 HttpResponse httpResponse,
                 SecurityKeyAuthenticationService service) =>
                {
                    SetNoStore(httpResponse);

                    if (!httpRequest.IsHttps)
                    {
                        return HttpsRequired();
                    }

                    try
                    {
                        var result = service.CreateAssertionOptions();

                        return Results.Ok(new
                        {
                            status = "touch-required",
                            transactionId = result.TransactionId,
                            options = result.Options
                        });
                    }
                    catch (SecurityKeyProtocolException exception)
                    {
                        return ToProblem(exception);
                    }
                })
            .RequireRateLimiting("security-key-session");

        app.MapPost(
                SessionCompletePath,
                async Task<IResult> (
                    SecurityKeyAssertionCompleteRequest request,
                    HttpRequest httpRequest,
                    HttpResponse httpResponse,
                    SecurityKeyAuthenticationService service,
                    CancellationToken cancellationToken) =>
                {
                    SetNoStore(httpResponse);

                    if (!httpRequest.IsHttps)
                    {
                        return HttpsRequired();
                    }

                    try
                    {
                        var result = await service.CompleteAssertionAsync(
                            request.TransactionId,
                            request.Response,
                            cancellationToken);

                        return Results.Ok(new
                        {
                            status = "authenticated",
                            authenticationMethod = "FIDO2",
                            sessionToken = result.SessionToken,
                            expiresUtc = result.ExpiresUtc,
                            keyLabel = result.KeyLabel
                        });
                    }
                    catch (SecurityKeyProtocolException exception)
                    {
                        return ToProblem(exception);
                    }
                })
            .RequireRateLimiting("security-key-session");
    }

    private static IResult HttpsRequired() =>
        Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "HTTPS erforderlich",
            detail: "Die Passkey-Anmeldung und -Registrierung sind ausschließlich über HTTPS zulässig.");

    private static IResult ToProblem(SecurityKeyProtocolException exception) =>
        Results.Problem(
            statusCode: exception.StatusCode,
            title: exception.Title,
            detail: exception.Message);

    private static void SetNoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
    }
}
