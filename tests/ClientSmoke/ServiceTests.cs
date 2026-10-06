using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TanssSystemCapture.Client.Models;
using TanssSystemCapture.Client.Services;
using Xunit;

public sealed class ServiceTests
{
    [Fact]
    public void CatalogMatcherSelectsDeviceManufacturerAndCorrectWindowsEdition()
    {
        Assert.Equal(91, DeviceCatalogMatcher.FindManufacturer("WORTMANN AG", [new ManufacturerOption { Id = 91, Name = "Wortmann AG" }, new ManufacturerOption { Id = 92, Name = "Dell" }])?.Id);
        Assert.Null(DeviceCatalogMatcher.FindManufacturer("", [new ManufacturerOption { Id = 91, Name = "Wortmann AG" }]));
        Assert.Equal(13, DeviceCatalogMatcher.FindOperatingSystem("Microsoft Windows 11 Professional", [new OperatingSystemOption { Id = 12, Name = "Windows 11 Home" }, new OperatingSystemOption { Id = 13, Name = "Windows 11 Pro" }])?.Id);
    }
    [Fact]
    public void DatePolicyRejectsInvalidCalendarsAndRetainsExpectedFormat()
    {
        Assert.Equal("070425", ClientDatePolicy.Validate(" 070425 ", "Kaufdatum"));
        Assert.Equal(new DateOnly(2025, 4, 7), ClientDatePolicy.Parse("070425"));
        Assert.Throws<ArgumentException>(() => ClientDatePolicy.Validate("310225", "Kaufdatum"));
        Assert.Throws<ArgumentException>(() => ClientDatePolicy.Validate("2025-04-07", "Kaufdatum"));
    }
    [Fact]
    public async Task AuthenticationContractAndLogoutClearSession()
    {
        var deletes = 0;
        var handler = new Stub(request =>
        {
            if (request.Method == HttpMethod.Delete) { deletes++; Assert.Equal("Bearer", request.Headers.Authorization?.Scheme); return new(HttpStatusCode.NoContent); }
            return Json(new { status = "authenticated", authenticationMethod = "TOTP", sessionToken = "unit-test-session", expiresUtc = DateTimeOffset.UtcNow.AddMinutes(30) });
        });
        using var api = new ImportApiClient(new HttpClient(handler) { BaseAddress = new("https://example.invalid/") });
        await api.AuthenticateWithTotpAsync("123456", CancellationToken.None);
        Assert.True(api.HasSession);
        await api.LogoutAsync();
        Assert.False(api.HasSession);
        await api.LogoutAsync();
        Assert.Equal(1, deletes);
    }
    [Fact]
    public async Task InvalidJsonAndProblemCorrelationAreReportedWithoutRetryingHttpErrors()
    {
        var calls = 0;
        using var api = new ImportApiClient(new HttpClient(new Stub(_ =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"title\":\"Dienst nicht verfügbar\",\"detail\":\"Bitte später prüfen\"}") };
            response.Headers.Add("X-Correlation-ID", "trace-123");
            return response;
        })) { BaseAddress = new("https://example.invalid/") });
        var error = await Assert.ThrowsAsync<ImportApiException>(() => api.GetDeviceCatalogAsync(CancellationToken.None));
        Assert.Contains("trace-123", error.Message);
        Assert.Equal(1, calls);
        using var malformed = new ImportApiClient(new HttpClient(new Stub(_ => new(HttpStatusCode.OK) { Content = new StringContent("broken-json") })) { BaseAddress = new("https://example.invalid/") });
        await Assert.ThrowsAsync<ImportApiException>(() => malformed.GetDeviceCatalogAsync(CancellationToken.None));
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(callback(request));
    }
}
