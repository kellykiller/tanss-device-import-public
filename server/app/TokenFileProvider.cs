using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TnsApiImport;

public sealed record TokenStatus(
    bool Configured,
    bool JwtStructureValid,
    bool LocallyUsable,
    string State,
    DateTimeOffset? ExpiresUtc,
    int? DaysRemaining,
    string Message);

public sealed class TokenConfigurationException : Exception
{
    public TokenConfigurationException(string message)
        : base(message)
    {
    }
}

public sealed class TokenFileProvider
{
    public const int ExpirationWarningDays = 30;

    private readonly TanssOptions _options;

    public TokenFileProvider(IOptions<TanssOptions> options)
    {
        _options = options.Value;
    }

    public TokenStatus GetErpStatus()
    {
        _ = TryReadToken(
            _options.ErpTokenFile,
            "ERP-Token",
            out _,
            out var status);
        return status;
    }

    public TokenStatus GetDeviceManagementStatus()
    {
        _ = TryReadToken(
            _options.DeviceManagementTokenFile,
            "Geräteverwaltungs-Token",
            out _,
            out var status);
        return status;
    }

    public string GetErpApiTokenHeaderValue()
    {
        if (!TryReadToken(
                _options.ErpTokenFile,
                "ERP-Token",
                out var token,
                out var status))
        {
            throw new TokenConfigurationException(status.Message);
        }

        return $"Bearer {token}";
    }

    public string GetDeviceManagementApiTokenHeaderValue()
    {
        if (!TryReadToken(
                _options.DeviceManagementTokenFile,
                "Geräteverwaltungs-Token",
                out var token,
                out var status))
        {
            throw new TokenConfigurationException(status.Message);
        }

        return $"Bearer {token}";
    }

    private static bool TryReadToken(
        string tokenFile,
        string tokenLabel,
        out string token,
        out TokenStatus status)
    {
        token = string.Empty;

        string rawToken;

        try
        {
            if (!File.Exists(tokenFile))
            {
                status = NewErrorStatus(
                    "missing",
                    $"{tokenLabel}-Datei wurde nicht gefunden.");
                return false;
            }

            rawToken = File.ReadAllText(tokenFile).Trim();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            status = NewErrorStatus(
                "unreadable",
                $"{tokenLabel}-Datei kann nicht gelesen werden.");
            return false;
        }

        if (rawToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            rawToken = rawToken[7..];
        }

        token = string.Concat(rawToken.Where(character => !char.IsWhiteSpace(character)));

        if (string.IsNullOrEmpty(token))
        {
            status = NewErrorStatus(
                "empty",
                $"{tokenLabel}-Datei ist leer.");
            return false;
        }

        var segments = token.Split('.');

        if (segments.Length != 3 || segments.Any(string.IsNullOrWhiteSpace))
        {
            status = new TokenStatus(
                Configured: true,
                JwtStructureValid: false,
                LocallyUsable: false,
                State: "invalid",
                ExpiresUtc: null,
                DaysRemaining: null,
                Message: $"{tokenLabel} besitzt keine gültige JWT-Struktur.");
            return false;
        }

        if (!TryReadExpiration(segments[1], out var expiresUtc))
        {
            status = new TokenStatus(
                Configured: true,
                JwtStructureValid: false,
                LocallyUsable: false,
                State: "invalid",
                ExpiresUtc: null,
                DaysRemaining: null,
                Message: $"Ablaufdatum des {tokenLabel} konnte nicht gelesen werden.");
            return false;
        }

        var remaining = expiresUtc - DateTimeOffset.UtcNow;
        var daysRemaining = (int)Math.Ceiling(remaining.TotalDays);

        if (remaining <= TimeSpan.Zero)
        {
            status = new TokenStatus(
                Configured: true,
                JwtStructureValid: true,
                LocallyUsable: false,
                State: "expired",
                ExpiresUtc: expiresUtc,
                DaysRemaining: daysRemaining,
                Message: $"{tokenLabel} ist abgelaufen.");
            return false;
        }

        if (remaining <= TimeSpan.FromDays(ExpirationWarningDays))
        {
            status = new TokenStatus(
                Configured: true,
                JwtStructureValid: true,
                LocallyUsable: true,
                State: "warning",
                ExpiresUtc: expiresUtc,
                DaysRemaining: daysRemaining,
                Message: $"{tokenLabel} ist lokal lesbar und läuft in spätestens {ExpirationWarningDays} Tagen ab. TANSS prüft die Gültigkeit beim API-Aufruf.");
            return true;
        }

        status = new TokenStatus(
            Configured: true,
            JwtStructureValid: true,
            LocallyUsable: true,
            State: "ok",
            ExpiresUtc: expiresUtc,
            DaysRemaining: daysRemaining,
            Message: $"{tokenLabel} ist lokal lesbar und laut JWT-Ablaufdatum nicht abgelaufen. TANSS prüft die Gültigkeit beim API-Aufruf.");
        return true;
    }

    private static TokenStatus NewErrorStatus(string state, string message)
    {
        return new TokenStatus(
            Configured: false,
            JwtStructureValid: false,
            LocallyUsable: false,
            State: state,
            ExpiresUtc: null,
            DaysRemaining: null,
            Message: message);
    }

    private static bool TryReadExpiration(string jwtPayload, out DateTimeOffset expiresUtc)
    {
        expiresUtc = default;

        try
        {
            var base64 = jwtPayload
                .Replace('-', '+')
                .Replace('_', '/');

            var padding = (4 - base64.Length % 4) % 4;
            base64 = base64.PadRight(base64.Length + padding, '=');

            var payloadBytes = Convert.FromBase64String(base64);
            using var document = JsonDocument.Parse(payloadBytes);

            if (!document.RootElement.TryGetProperty("exp", out var expirationElement) ||
                !expirationElement.TryGetInt64(out var expirationSeconds))
            {
                return false;
            }

            expiresUtc = DateTimeOffset.FromUnixTimeSeconds(expirationSeconds);
            return true;
        }
        catch (Exception exception) when (
            exception is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}

public sealed class TokenExpiryMonitor : BackgroundService
{
    private readonly TokenFileProvider _tokenProvider;
    private readonly ILogger<TokenExpiryMonitor> _logger;

    public TokenExpiryMonitor(
        TokenFileProvider tokenProvider,
        ILogger<TokenExpiryMonitor> logger)
    {
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            LogTokenStatus("ERP-Token", _tokenProvider.GetErpStatus());
            LogTokenStatus(
                "Geräteverwaltungs-Token",
                _tokenProvider.GetDeviceManagementStatus());

            try
            {
                await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void LogTokenStatus(string tokenLabel, TokenStatus status)
    {
        if (!status.LocallyUsable)
        {
            _logger.LogError(
                "{TokenLabel}-Status: {TokenState}. {TokenMessage}",
                tokenLabel,
                status.State,
                status.Message);
        }
        else if (string.Equals(status.State, "warning", StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "{TokenLabel} läuft am {ExpirationDate:u} ab. Verbleibende Tage: {DaysRemaining}.",
                tokenLabel,
                status.ExpiresUtc,
                status.DaysRemaining);
        }
        else
        {
            _logger.LogInformation(
                "{TokenLabel} ist lokal lesbar und läuft am {ExpirationDate:u} ab. TANSS prüft die Gültigkeit beim API-Aufruf.",
                tokenLabel,
                status.ExpiresUtc);
        }
    }
}
