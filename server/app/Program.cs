using System.Net;
using System.Security.Authentication;
using System.Threading.RateLimiting;
using Fido2NetLib;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using TnsApiImport;

const string ServiceVersion = "0.18.2";
const string TotpSessionPath = "/api/v1/auth/totp/session";

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.ConfigureHttpsDefaults(httpsOptions =>
    {
        httpsOptions.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
    });
});

builder.Services
    .AddOptions<TanssOptions>()
    .Bind(builder.Configuration.GetSection(TanssOptions.SectionName))
    .ValidateOnStart();

builder.Services
    .AddOptions<AuthenticationOptions>()
    .Bind(builder.Configuration.GetSection(AuthenticationOptions.SectionName))
    .ValidateOnStart();

builder.Services
    .AddOptions<SecurityKeyOptions>()
    .Bind(builder.Configuration.GetSection(SecurityKeyOptions.SectionName))
    .ValidateOnStart();

var securityKeyConfiguration = builder.Configuration
    .GetSection(SecurityKeyOptions.SectionName)
    .Get<SecurityKeyOptions>() ?? new SecurityKeyOptions();

builder.Services.AddFido2(configuration =>
{
    configuration.RPID = securityKeyConfiguration.RelyingPartyId;
    configuration.RPName = securityKeyConfiguration.RelyingPartyName;
    configuration.Origins = securityKeyConfiguration.Origins
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    configuration.Timeout = checked(
        (uint)TimeSpan.FromMinutes(
            securityKeyConfiguration.ChallengeMinutes).TotalMilliseconds);
    configuration.ChallengeSize = 32;
});

builder.Services.AddSapSerialLookup(builder.Configuration);

builder.Services.AddSingleton<IValidateOptions<TanssOptions>, TanssOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<AuthenticationOptions>, AuthenticationOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<SecurityKeyOptions>, SecurityKeyOptionsValidator>();
builder.Services.AddSingleton<TokenFileProvider>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TotpAuthenticationService>();
builder.Services.AddSingleton<SecurityKeyStateStore>();
builder.Services.AddScoped<SecurityKeyAuthenticationService>();
builder.Services.AddScoped<DeviceCatalogService>();
builder.Services.AddScoped<DeviceOperationService>();
builder.Services.AddHostedService<TokenExpiryMonitor>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("totp-session", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddPolicy("security-key-session", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddPolicy("security-key-enrollment", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

builder.Services.AddHttpClient<TanssErpClient>((serviceProvider, client) =>
{
    var options = serviceProvider
        .GetRequiredService<IOptions<TanssOptions>>()
        .Value;

    client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(20);
});

builder.Services.AddHttpClient<TanssDeviceManagementClient>((serviceProvider, client) =>
{
    var options = serviceProvider
        .GetRequiredService<IOptions<TanssOptions>>()
        .Value;

    client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(60);
});

builder.Services.AddHttpClient<WortmannWarrantyClient>(client =>
{
    client.BaseAddress = new Uri("https://www.wortmann.de/", UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(12);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "TANSS-Device-Import/0.18.2 (+https://www.wortmann.de/)");
});

var app = builder.Build();

app.UseRateLimiter();

app.Use(async (httpContext, next) =>
{
    if (httpContext.Request.Path.Equals("/health"))
    {
        await next();
        return;
    }

    if (!httpContext.Request.IsHttps)
    {
        await Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "HTTPS erforderlich",
            detail: "Außer dem lokalen Liveness-Endpunkt sind alle Importdienst-Endpunkte ausschließlich über HTTPS zulässig.")
            .ExecuteAsync(httpContext);
        return;
    }

    if (httpContext.Request.Path.Equals(TotpSessionPath) ||
        SecurityKeyModule.IsAnonymousAuthenticationPath(httpContext.Request.Path))
    {
        await next();
        return;
    }

    var authorizationHeader = httpContext.Request.Headers.Authorization.ToString();
    const string bearerPrefix = "Bearer ";
    var sessionToken = authorizationHeader.StartsWith(
        bearerPrefix,
        StringComparison.OrdinalIgnoreCase)
        ? authorizationHeader[bearerPrefix.Length..].Trim()
        : null;
    var totpAuthentication = httpContext.RequestServices
        .GetRequiredService<TotpAuthenticationService>();
    var securityKeyAuthentication = httpContext.RequestServices
        .GetRequiredService<SecurityKeyStateStore>();

    if (totpAuthentication.ValidateSession(sessionToken) ||
        securityKeyAuthentication.ValidateSession(sessionToken))
    {
        await next();
        return;
    }

    await Results.Problem(
        statusCode: StatusCodes.Status401Unauthorized,
        title: "Anmeldung am Importdienst erforderlich",
        detail: "Für diese HTTPS-Anfrage ist eine gültige Passkey- oder TOTP-Sitzung erforderlich.")
        .ExecuteAsync(httpContext);
});

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "TANSS Device Import API",
    version = ServiceVersion,
    utc = DateTimeOffset.UtcNow
}));

app.MapSapSerialLookup();
app.MapSecurityKeyAuthentication();

app.MapGet(
    "/status",
    (TokenFileProvider tokenProvider,
     TotpAuthenticationService totpAuthentication,
     SecurityKeyStateStore securityKeyAuthentication) =>
{
    var erpTokenStatus = tokenProvider.GetErpStatus();
    var deviceTokenStatus = tokenProvider.GetDeviceManagementStatus();
    var totpStatus = totpAuthentication.GetStatus();
    var securityKeyStatus = securityKeyAuthentication.GetStatus();
    var allTokensUsable =
        erpTokenStatus.LocallyUsable && deviceTokenStatus.LocallyUsable;
    var hasWarning =
        string.Equals(erpTokenStatus.State, "warning", StringComparison.Ordinal) ||
        string.Equals(deviceTokenStatus.State, "warning", StringComparison.Ordinal);
    var hasAuthenticationMethod =
        securityKeyStatus.Configured ||
        totpStatus.Configured;
    var serviceStatus = !allTokensUsable || !hasAuthenticationMethod
        ? "error"
        : hasWarning
            ? "warning"
            : "ok";

    return Results.Json(
        new
        {
            status = serviceStatus,
            service = "TANSS Device Import API",
            version = ServiceVersion,
            utc = DateTimeOffset.UtcNow,
            erpToken = new
            {
                erpTokenStatus.Configured,
                erpTokenStatus.JwtStructureValid,
                erpTokenStatus.LocallyUsable,
                erpTokenStatus.State,
                erpTokenStatus.ExpiresUtc,
                erpTokenStatus.DaysRemaining,
                erpTokenStatus.Message
            },
            deviceManagementToken = new
            {
                deviceTokenStatus.Configured,
                deviceTokenStatus.JwtStructureValid,
                deviceTokenStatus.LocallyUsable,
                deviceTokenStatus.State,
                deviceTokenStatus.ExpiresUtc,
                deviceTokenStatus.DaysRemaining,
                deviceTokenStatus.Message
            },
            passkey = new
            {
                acceptedOnHttps = true,
                touchOnlyRequested = true,
                userVerification = "discouraged",
                authenticatorAttachment = "cross-platform",
                securityKeyStatus.Configured,
                securityKeyStatus.State,
                securityKeyStatus.RegisteredKeyCount,
                securityKeyStatus.SessionMinutes,
                securityKeyStatus.Message
            },
            totp = new
            {
                acceptedOnHttps = true,
                totpStatus.Configured,
                totpStatus.State,
                totpStatus.SessionMinutes,
                totpStatus.Message
            }
        },
        statusCode: allTokensUsable && hasAuthenticationMethod
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable);
});

app.MapPost(
    TotpSessionPath,
    (TotpSessionRequest request,
     HttpRequest httpRequest,
     HttpResponse httpResponse,
     TotpAuthenticationService totpAuthentication) =>
    {
        httpResponse.Headers.CacheControl = "no-store";
        httpResponse.Headers.Pragma = "no-cache";

        if (!httpRequest.IsHttps)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "HTTPS erforderlich",
                detail: "Die TOTP-Anmeldung ist ausschließlich über HTTPS zulässig.");
        }

        var result = totpAuthentication.CreateSession(request.Code);

        return result.State switch
        {
            TotpSessionCreationState.Success => Results.Ok(new
            {
                status = "authenticated",
                authenticationMethod = "TOTP",
                sessionToken = result.SessionToken,
                expiresUtc = result.ExpiresUtc
            }),
            TotpSessionCreationState.ReusedCode => Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "TOTP-Code bereits verwendet",
                detail: "Dieser TOTP-Code wurde bereits erfolgreich verwendet. Bitte den nächsten sechsstelligen Code abwarten."),
            TotpSessionCreationState.NotConfigured => Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "TOTP nicht konfiguriert",
                detail: "Auf dem Importserver ist kein verwendbarer TOTP-Schlüssel eingerichtet."),
            _ => Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "TOTP-Code ungültig",
                detail: "Der sechsstellige TOTP-Code ist ungültig oder abgelaufen.")
        };
    })
    .RequireRateLimiting("totp-session");

app.MapGet(
    "/api/v1/companies/by-customer-number/{customerNumber}",
    async Task<IResult> (
        string customerNumber,
        TanssErpClient tanssClient,
        CancellationToken cancellationToken) =>
    {
        customerNumber = customerNumber.Trim();

        if (customerNumber.Length is < 1 or > 50)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Ungültige Kundennummer",
                detail: "Die Kundennummer muss zwischen 1 und 50 Zeichen lang sein.");
        }

        try
        {
            var company = await tanssClient.FindCompanyByCustomerNumberAsync(
                customerNumber,
                cancellationToken);

            if (company is null)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Kunde nicht gefunden",
                    detail: $"Kundennummer {customerNumber} wurde in TANSS nicht exakt gefunden.");
            }

            return Results.Ok(new
            {
                status = "ok",
                company
            });
        }
        catch (TokenConfigurationException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "ERP-Token nicht verwendbar",
                detail: exception.Message);
        }
        catch (CompanyNotUniqueException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Kundennummer nicht eindeutig",
                detail: exception.Message);
        }
        catch (TanssApiException exception)
            when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "TANSS-Authentifizierung fehlgeschlagen",
                detail: $"TANSS-{exception.InterfaceName} antwortete mit HTTP {(int)exception.StatusCode}.");
        }
        catch (TanssApiException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "TANSS-Anfrage fehlgeschlagen",
                detail: $"TANSS-{exception.InterfaceName} antwortete mit HTTP {(int)exception.StatusCode}.");
        }
        catch (InvalidTanssResponseException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Unerwartete TANSS-Antwort",
                detail: exception.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status504GatewayTimeout,
                title: "TANSS-Zeitüberschreitung",
                detail: "TANSS hat nicht innerhalb des festgelegten Zeitlimits geantwortet.");
        }
        catch (HttpRequestException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "TANSS nicht erreichbar",
                detail: "Die Verbindung zur TANSS-API konnte nicht hergestellt werden.");
        }
    });

app.MapGet(
    "/api/v1/companies/by-customer-number/{customerNumber}/devices/count",
    async Task<IResult> (
        string customerNumber,
        TanssErpClient tanssErpClient,
        TanssDeviceManagementClient tanssDeviceClient,
        CancellationToken cancellationToken) =>
    {
        customerNumber = customerNumber.Trim();

        if (customerNumber.Length is < 1 or > 50)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Ungültige Kundennummer",
                detail: "Die Kundennummer muss zwischen 1 und 50 Zeichen lang sein.");
        }

        try
        {
            var company = await tanssErpClient.FindCompanyByCustomerNumberAsync(
                customerNumber,
                cancellationToken);

            if (company is null)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Kunde nicht gefunden",
                    detail: $"Kundennummer {customerNumber} wurde in TANSS nicht exakt gefunden.");
            }

            var deviceCount = await tanssDeviceClient.CountCompanyDevicesAsync(
                company.Id,
                cancellationToken);

            return Results.Ok(new
            {
                status = "ok",
                company = new
                {
                    company.Id,
                    company.CustomerNumber,
                    company.Name
                },
                filters = new
                {
                    branches = "COMPANY_ONLY",
                    active = "ACTIVE_AND_INACTIVE",
                    servers = "SERVERS_AND_PCS"
                },
                deviceCount
            });
        }
        catch (TokenConfigurationException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "TANSS-Token nicht verwendbar",
                detail: exception.Message);
        }
        catch (CompanyNotUniqueException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Kundennummer nicht eindeutig",
                detail: exception.Message);
        }
        catch (TanssApiException exception)
            when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "TANSS-Authentifizierung fehlgeschlagen",
                detail: $"TANSS-{exception.InterfaceName} antwortete mit HTTP {(int)exception.StatusCode}.");
        }
        catch (TanssApiException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "TANSS-Anfrage fehlgeschlagen",
                detail: $"TANSS-{exception.InterfaceName} antwortete mit HTTP {(int)exception.StatusCode}.");
        }
        catch (InvalidTanssResponseException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Unerwartete TANSS-Antwort",
                detail: exception.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status504GatewayTimeout,
                title: "TANSS-Zeitüberschreitung",
                detail: "TANSS hat nicht innerhalb des festgelegten Zeitlimits geantwortet.");
        }
        catch (HttpRequestException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "TANSS nicht erreichbar",
                detail: "Die Verbindung zur TANSS-API konnte nicht hergestellt werden.");
        }
    });

app.MapGet(
    "/api/v1/companies/{companyId:long}/hosts/{hostId:long}",
    async Task<IResult> (
        long companyId,
        long hostId,
        TanssDeviceManagementClient tanssDeviceClient,
        TanssErpClient tanssErpClient,
        CancellationToken cancellationToken) =>
    {
        if (companyId <= 0 || hostId <= 0)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Ungültige TANSS-ID",
                detail: "Firmen-ID und Host-ID müssen größer als null sein.");
        }

        try
        {
            var host = await tanssDeviceClient.GetPcByIdAsync(
                hostId,
                cancellationToken);

            if (host is null)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Host nicht gefunden",
                    detail: $"Unter der TANSS-ID {hostId} wurde kein PC oder Server gefunden.");
            }

            if (host.CompanyId != companyId)
            {
                var actualCompany = await tanssErpClient.FindCompanyByIdAsync(
                    host.CompanyId,
                    cancellationToken);
                var actualCustomer = actualCompany is null
                    ? $"interne TANSS-Firmen-ID {host.CompanyId}"
                    : $"Kunde {actualCompany.CustomerNumber} – {actualCompany.Name} " +
                      $"(interne TANSS-ID {actualCompany.Id})";

                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Host gehört zu einem anderen Kunden",
                    detail: $"TANSS-ID {host.Id} ist {actualCustomer} zugeordnet.");
            }

            return Results.Ok(new
            {
                status = "ok",
                host
            });
        }
        catch (TokenConfigurationException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Geräteverwaltungs-Token nicht verwendbar",
                detail: exception.Message);
        }
        catch (TanssApiException exception)
            when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Host nicht gefunden",
                detail: $"Unter der TANSS-ID {hostId} wurde kein PC oder Server gefunden.");
        }
        catch (TanssApiException exception)
            when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "TANSS-Authentifizierung fehlgeschlagen",
                detail: $"TANSS-{exception.InterfaceName} antwortete mit HTTP {(int)exception.StatusCode}.");
        }
        catch (TanssApiException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "TANSS-Anfrage fehlgeschlagen",
                detail: $"TANSS-{exception.InterfaceName} antwortete mit HTTP {(int)exception.StatusCode}.");
        }
        catch (InvalidTanssResponseException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Unerwartete TANSS-Antwort",
                detail: exception.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status504GatewayTimeout,
                title: "TANSS-Zeitüberschreitung",
                detail: "TANSS hat nicht innerhalb des festgelegten Zeitlimits geantwortet.");
        }
        catch (HttpRequestException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "TANSS nicht erreichbar",
                detail: "Die Verbindung zur TANSS-API konnte nicht hergestellt werden.");
        }
    });

app.MapGet(
    "/api/v1/device-catalogs",
    async Task<IResult> (
        DeviceCatalogService catalogService,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var catalog = await catalogService.GetAllowedAsync(cancellationToken);
            return Results.Ok(new
            {
                status = "ok",
                manufacturers = catalog.Manufacturers,
                operatingSystems = catalog.OperatingSystems
            });
        }
        catch (TokenConfigurationException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "TANSS-Token nicht verwendbar",
                detail: exception.Message);
        }
        catch (TanssApiException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "TANSS-Katalogabfrage fehlgeschlagen",
                detail: $"TANSS-{exception.InterfaceName} antwortete mit HTTP {(int)exception.StatusCode}.");
        }
        catch (InvalidTanssResponseException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Unerwartete TANSS-Katalogantwort",
                detail: exception.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status504GatewayTimeout,
                title: "TANSS-Zeitüberschreitung",
                detail: "Die TANSS-Kataloge haben nicht rechtzeitig geantwortet.");
        }
        catch (HttpRequestException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "TANSS nicht erreichbar",
                detail: "Die TANSS-Kataloge konnten nicht abgerufen werden.");
        }
    });

app.MapGet(
    "/api/v1/devices/by-serial-number",
    async Task<IResult> (
        string serialNumber,
        long selectedCompanyId,
        TanssDeviceManagementClient tanssDeviceClient,
        TanssErpClient tanssErpClient,
        CancellationToken cancellationToken) =>
    {
        serialNumber = serialNumber.Trim();

        if (serialNumber.Length is < 1 or > 200)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Ungültige Seriennummer",
                detail: "Die Seriennummer muss zwischen 1 und 200 Zeichen lang sein.");
        }

        if (selectedCompanyId <= 0)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Ungültige TANSS-Firmen-ID",
                detail: "Die ausgewählte interne TANSS-Firmen-ID muss größer als null sein.");
        }

        try
        {
            var matches = await tanssDeviceClient.FindPcsBySerialNumberAsync(
                serialNumber,
                cancellationToken);
            var companies = new Dictionary<long, CompanyResult>();

            foreach (var companyId in matches
                         .Select(match => match.CompanyId)
                         .Distinct())
            {
                var company = await tanssErpClient.FindCompanyByIdAsync(
                    companyId,
                    cancellationToken);

                if (company is null)
                {
                    throw new InvalidTanssResponseException(
                        $"Die zugeordnete interne TANSS-Firmen-ID {companyId} konnte nicht aufgelöst werden.");
                }

                companies.Add(companyId, company);
            }

            var safeMatches = matches
                .Select(match => new
                {
                    match.Id,
                    match.Name,
                    match.SerialNumber,
                    match.Active,
                    match.Server,
                    belongsToSelectedCompany = match.CompanyId == selectedCompanyId,
                    company = new
                    {
                        id = companies[match.CompanyId].Id,
                        customerNumber = companies[match.CompanyId].CustomerNumber,
                        name = companies[match.CompanyId].Name
                    }
                })
                .ToArray();

            return Results.Ok(new
            {
                status = "ok",
                serialNumber,
                matchCount = safeMatches.Length,
                matches = safeMatches
            });
        }
        catch (TokenConfigurationException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "TANSS-Token nicht verwendbar",
                detail: exception.Message);
        }
        catch (TanssApiException exception)
            when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "TANSS-Authentifizierung fehlgeschlagen",
                detail: $"TANSS-{exception.InterfaceName} antwortete mit HTTP {(int)exception.StatusCode}.");
        }
        catch (TanssApiException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "TANSS-Anfrage fehlgeschlagen",
                detail: $"TANSS-{exception.InterfaceName} antwortete mit HTTP {(int)exception.StatusCode}.");
        }
        catch (InvalidTanssResponseException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Unerwartete TANSS-Antwort",
                detail: exception.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status504GatewayTimeout,
                title: "TANSS-Zeitüberschreitung",
                detail: "TANSS hat nicht innerhalb des festgelegten Zeitlimits geantwortet.");
        }
        catch (HttpRequestException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "TANSS nicht erreichbar",
                detail: "Die Verbindung zur TANSS-API konnte nicht hergestellt werden.");
        }
    });

app.MapPost(
    "/api/v1/warranties/wortmann/lookup",
    async Task<IResult> (
        WortmannWarrantyLookupRequest request,
        WortmannWarrantyClient warrantyClient,
        CancellationToken cancellationToken) =>
    {
        var serialNumber = request.SerialNumber?.Trim() ?? string.Empty;

        if (serialNumber.Length is < 1 or > 200)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Ungültige Seriennummer",
                detail: "Die Seriennummer muss zwischen 1 und 200 Zeichen lang sein.");
        }

        try
        {
            var warranty = await warrantyClient.FindAsync(
                serialNumber,
                cancellationToken);

            if (warranty is null)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Keine Wortmann-Garantiedaten gefunden",
                    detail: "Die offizielle Wortmann-Seriennummernsuche lieferte für diese Seriennummer keine auswertbaren Daten.");
            }

            return Results.Ok(new
            {
                status = "ok",
                source = "https://www.wortmann.de/de-de/profile/snsearch.aspx",
                warranty = new
                {
                    warranty.SerialNumber,
                    warranty.ArticleNumber,
                    warranty.ProductDescription,
                    serviceStart = warranty.ServiceStart.ToString("ddMMyy"),
                    serviceEnd = warranty.ServiceEnd.ToString("ddMMyy"),
                    warranty.GuaranteeMonth,
                    warranty.ServiceCode,
                    warranty.ServiceDescription
                }
            });
        }
        catch (WortmannWarrantyException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Wortmann-Antwort nicht auswertbar",
                detail: exception.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status504GatewayTimeout,
                title: "Wortmann-Zeitüberschreitung",
                detail: "Die offizielle Wortmann-Seriennummernsuche hat nicht rechtzeitig geantwortet.");
        }
        catch (HttpRequestException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Wortmann nicht erreichbar",
                detail: $"Die offizielle Wortmann-Seriennummernsuche konnte nicht abgerufen werden: {exception.Message}");
        }
    });

app.MapPost(
    "/api/v1/companies/{companyId:long}/devices/transfer-preview",
    async Task<IResult> (
        long companyId,
        DeviceTransferPreviewRequest request,
        DeviceOperationService operationService,
        TanssDeviceManagementClient tanssDeviceClient,
        TanssErpClient tanssErpClient,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var company = await tanssErpClient.FindCompanyByIdAsync(
                companyId,
                cancellationToken);

            if (company is null)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Kunde nicht gefunden",
                    detail: $"Die interne TANSS-Firmen-ID {companyId} wurde nicht gefunden.");
            }

            var plan = await operationService.PrepareAsync(
                companyId,
                request,
                cancellationToken);
            object? validatedHost = null;

            if (request.HostId is long hostId)
            {
                var host = await tanssDeviceClient.GetPcByIdAsync(
                    hostId,
                    cancellationToken);

                if (host is null)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status404NotFound,
                        title: "VM-Host nicht gefunden",
                        detail: $"Unter der TANSS-ID {hostId} wurde kein PC oder Server gefunden.");
                }

                if (host.CompanyId != companyId)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status409Conflict,
                        title: "VM-Host gehört zu einem anderen Kunden",
                        detail: $"TANSS-ID {host.Id} ist der internen TANSS-Firmen-ID {host.CompanyId} zugeordnet.");
                }

                validatedHost = new
                {
                    host.Id,
                    host.CompanyId,
                    host.Name,
                    host.Active,
                    host.Server,
                    host.HostId
                };
            }

            object? targetDevice = plan.TargetDevice is null
                ? null
                : new
                {
                    plan.TargetDevice.Id,
                    plan.TargetDevice.CompanyId,
                    plan.TargetDevice.Name,
                    plan.TargetDevice.Active,
                    plan.TargetDevice.Server,
                    plan.TargetDevice.HostId
                };

            return Results.Ok(new
            {
                status = "ok",
                writePerformed = false,
                company = new
                {
                    company.Id,
                    company.CustomerNumber,
                    company.Name
                },
                validatedHost,
                targetDevice,
                writeMode = plan.Mode.ToString().ToUpperInvariant(),
                writeAction = plan.Mode switch
                {
                    DeviceWriteMode.Supplement => "Vorhandenes System nur ergänzen",
                    DeviceWriteMode.Overwrite => "Ausgewählte Werte überschreiben",
                    _ => "Neues System anlegen"
                },
                documentedTanssOperation = plan.DocumentedTanssOperation,
                bridgeTarget = plan.BridgeTarget,
                previewSha256 = plan.PreviewSha256,
                canCreate = plan.CanWrite,
                canWrite = plan.CanWrite,
                mappedFields = plan.MappedFields,
                warnings = plan.Warnings,
                blockingIssues = plan.BlockingIssues,
                blockingFields = plan.BlockingFields,
                requestBody = plan.RequestBody
            });
        }
        catch (TransferPreviewValidationException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Ungültige Übertragungsauswahl",
                detail: exception.Message);
        }
        catch (TokenConfigurationException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "TANSS-Token nicht verwendbar",
                detail: exception.Message);
        }
        catch (TanssApiException exception)
            when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "TANSS-Eintrag nicht gefunden",
                detail: "Ein für die Vorschau benötigter TANSS-Eintrag wurde nicht gefunden.");
        }
        catch (TanssApiException exception)
            when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "TANSS-Authentifizierung fehlgeschlagen",
                detail: $"TANSS-{exception.InterfaceName} antwortete mit HTTP {(int)exception.StatusCode}.");
        }
        catch (TanssApiException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "TANSS-Anfrage fehlgeschlagen",
                detail: exception.ResponseDetail ??
                    $"TANSS-{exception.InterfaceName} antwortete mit HTTP {(int)exception.StatusCode}.");
        }
        catch (InvalidTanssResponseException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Unerwartete TANSS-Antwort",
                detail: exception.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status504GatewayTimeout,
                title: "TANSS-Zeitüberschreitung",
                detail: "TANSS hat nicht innerhalb des festgelegten Zeitlimits geantwortet.");
        }
        catch (HttpRequestException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "TANSS nicht erreichbar",
                detail: "Die Verbindung zur TANSS-API konnte nicht hergestellt werden.");
        }
    });

app.MapPost(
    "/api/v1/companies/{companyId:long}/devices",
    async Task<IResult> (
        long companyId,
        DeviceCreateRequest createRequest,
        HttpRequest httpRequest,
        DeviceOperationService operationService,
        TanssDeviceManagementClient tanssDeviceClient,
        TanssErpClient tanssErpClient,
        IOptions<TanssOptions> tanssOptions,
        CancellationToken cancellationToken) =>
    {
        if (!httpRequest.IsHttps)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "HTTPS erforderlich",
                detail: "Eine TANSS-Geräteanlage oder -aktualisierung ist ausschließlich über die authentifizierte HTTPS-Schnittstelle zulässig.");
        }

        if (createRequest.Selection is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Ungültiger Übertragungsauftrag",
                detail: "Die zuvor geprüfte Übertragungsauswahl fehlt.");
        }

        try
        {
            var company = await tanssErpClient.FindCompanyByIdAsync(
                companyId,
                cancellationToken);

            if (company is null)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Kunde nicht gefunden",
                    detail: $"Die interne TANSS-Firmen-ID {companyId} wurde nicht gefunden.");
            }

            var plan = await operationService.PrepareAsync(
                companyId,
                createRequest.Selection,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(createRequest.ExpectedPreviewSha256) ||
                !string.Equals(
                    createRequest.ExpectedPreviewSha256.Trim(),
                    plan.PreviewSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Übertragungsvorschau nicht mehr aktuell",
                    detail: "Die Übertragungswerte oder der vorhandene TANSS-Eintrag stimmen nicht mehr mit der bestätigten Vorschau überein. Bitte eine neue Vorschau erstellen.");
            }

            if (!plan.CanWrite)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "TANSS-Übertragung blockiert",
                    detail: string.Join(" ", plan.BlockingIssues));
            }

            if (createRequest.Selection.HostId is long hostId)
            {
                var host = await tanssDeviceClient.GetPcByIdAsync(
                    hostId,
                    cancellationToken);

                if (host is null || host.CompanyId != companyId)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status409Conflict,
                        title: "VM-Host nicht verwendbar",
                        detail: "Der bestätigte VM-Host wurde nicht gefunden oder gehört nicht mehr zum ausgewählten Kunden.");
                }
            }

            var writtenDevice = DeviceWritePlanner.IsUpdate(plan.Mode)
                ? await tanssDeviceClient.UpdatePcAsync(
                    plan.TargetDeviceId!.Value,
                    plan.RequestBody,
                    cancellationToken)
                : await tanssDeviceClient.CreatePcAsync(
                    plan.RequestBody,
                    cancellationToken);

            if (writtenDevice.CompanyId != companyId)
            {
                throw new InvalidTanssResponseException(
                    "TANSS meldete nach der Übertragung eine abweichende Kundenzuordnung.");
            }

            var responseBody = new
            {
                status = DeviceWritePlanner.IsUpdate(plan.Mode)
                    ? "updated"
                    : "created",
                writeMode = plan.Mode.ToString().ToUpperInvariant(),
                company = new
                {
                    company.Id,
                    company.CustomerNumber,
                    company.Name
                },
                device = writtenDevice,
                previewSha256 = plan.PreviewSha256,
                deviceUrl = string.IsNullOrWhiteSpace(
                    tanssOptions.Value.DeviceWebUrlTemplate)
                    ? null
                    : tanssOptions.Value.DeviceWebUrlTemplate.Replace(
                        "{deviceId}",
                        writtenDevice.Id.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        StringComparison.Ordinal)
            };

            if (DeviceWritePlanner.IsUpdate(plan.Mode))
            {
                return Results.Ok(responseBody);
            }

            return Results.Created(
                $"/api/v1/companies/{companyId}/hosts/{writtenDevice.Id}",
                responseBody);
        }
        catch (TransferPreviewValidationException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Ungültige Übertragungsauswahl",
                detail: exception.Message);
        }
        catch (TokenConfigurationException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "TANSS-Token nicht verwendbar",
                detail: exception.Message);
        }
        catch (TanssApiException exception)
            when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "TANSS-Authentifizierung fehlgeschlagen",
                detail: $"TANSS-{exception.InterfaceName} antwortete mit HTTP {(int)exception.StatusCode}.");
        }
        catch (TanssApiException exception)
            when (exception.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "TANSS hat die Übertragung abgelehnt",
                detail: exception.ResponseDetail ??
                    $"TANSS-{exception.InterfaceName} antwortete mit HTTP {(int)exception.StatusCode}.");
        }
        catch (TanssApiException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "TANSS-Übertragung fehlgeschlagen",
                detail: exception.ResponseDetail ??
                    $"TANSS-{exception.InterfaceName} antwortete mit HTTP {(int)exception.StatusCode}.");
        }
        catch (InvalidTanssResponseException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Unerwartete TANSS-Antwort nach der Übertragung",
                detail: exception.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status504GatewayTimeout,
                title: "Zeitüberschreitung bei der TANSS-Übertragung",
                detail: "Das Ergebnis ist unklar. Vor einem erneuten Versuch bitte den vorhandenen TANSS-Eintrag prüfen.");
        }
        catch (HttpRequestException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "TANSS nicht erreichbar",
                detail: "Die Verbindung zur TANSS-API konnte nicht hergestellt werden.");
        }
    });

app.Run();
