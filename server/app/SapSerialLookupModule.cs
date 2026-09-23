using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace TnsApiImport;

public static class SapSerialLookupModule
{
    private const string RateLimitPolicyName = "sap-lookup";

    public static IServiceCollection AddSapSerialLookup(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<SapOptions>()
            .Bind(configuration.GetSection(SapOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<
            IValidateOptions<SapOptions>,
            SapOptionsValidator>();

        services.AddScoped<SapSerialLookupService>();

        services.AddRateLimiter(options =>
        {
            options.AddPolicy(
                RateLimitPolicyName,
                httpContext =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey:
                            httpContext.Connection.RemoteIpAddress?.ToString() ??
                            "unknown",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 30,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                            AutoReplenishment = true
                        }));
        });

        return services;
    }

    public static IEndpointRouteBuilder MapSapSerialLookup(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapGet(
                "/api/v1/sap/items/by-serial",
                HandleLookupAsync)
            .RequireRateLimiting(RateLimitPolicyName);

        return endpoints;
    }

    private static async Task<IResult> HandleLookupAsync(
        string serialNumber,
        HttpResponse response,
        SapSerialLookupService lookupService,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";

        var logger = loggerFactory.CreateLogger(
            "TnsApiImport.SapSerialLookup");

        try
        {
            var result =
                await lookupService.FindBySerialNumberAsync(
                    serialNumber,
                    cancellationToken);

            return Results.Ok(new
            {
                status = "ok",
                source = "SAP_BUSINESS_ONE",
                result.SerialNumber,
                result.Resolution,
                result.MatchCount,
                result.ItemCode,
                itemCodes = result.ItemCodes
            });
        }
        catch (ArgumentException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Ungültige Seriennummer",
                detail: exception.Message);
        }
        catch (SapLookupConfigurationException exception)
        {
            logger.LogWarning(
                "Die SAP-Seriennummernsuche ist nicht verwendbar: {Message}",
                exception.Message);

            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "SAP-Suche nicht verfügbar",
                detail: exception.Message);
        }
        catch (SqlException exception)
            when (exception.Number == -2)
        {
            logger.LogWarning(
                "Zeitüberschreitung bei der SAP-SQL-Abfrage.");

            return Results.Problem(
                statusCode: StatusCodes.Status504GatewayTimeout,
                title: "SAP-Zeitüberschreitung",
                detail:
                    "SAP Business One hat nicht innerhalb des " +
                    "festgelegten Zeitlimits geantwortet.");
        }
        catch (SqlException exception)
        {
            logger.LogError(
                "SAP-SQL-Abfrage fehlgeschlagen. " +
                "SQL-Fehlernummer={SqlErrorNumber}, " +
                "Status={SqlErrorState}, Klasse={SqlErrorClass}.",
                exception.Number,
                exception.State,
                exception.Class);

            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "SAP-Suche nicht verfügbar",
                detail:
                    "Die Verbindung zu SAP Business One oder die " +
                    "Seriennummernabfrage ist fehlgeschlagen.");
        }
        catch (TaskCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Zeitüberschreitung bei der SAP-Seriennummernsuche.");

            return Results.Problem(
                statusCode: StatusCodes.Status504GatewayTimeout,
                title: "SAP-Zeitüberschreitung",
                detail:
                    "SAP Business One hat nicht innerhalb des " +
                    "festgelegten Zeitlimits geantwortet.");
        }
    }
}
