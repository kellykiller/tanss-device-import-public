using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using TanssSystemCapture.Client;
using TanssSystemCapture.Client.Models;
using TanssSystemCapture.Client.Services;

internal static class TransferSelectionTests
{
    public static async Task RunAsync()
    {
        var policy = 2;
        var foreign = false;
        using var handler = new FakeHandler(async request =>
        {
            Require(request.RequestUri!.AbsolutePath == "/api/v1/companies/42/devices/resolve-target", "Unexpected lookup");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Require(body.RootElement.GetProperty("serialNumber").GetString() == "DEMO0002" && body.RootElement.GetProperty("name").GetString() == "DEMO-SERVER-01", "Unchecked lookup fields lost");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                selectionPolicyVersion = policy,
                blockingIssues = foreign ? new[] { "System gehört einem anderen Kunden" } : Array.Empty<string>(),
                targetDevice = new { id = 9001, companyId = 42, name = "DEMO-SERVER-01", active = true }
            }), Encoding.UTF8, "application/json") };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid/") };
        using var api = new ImportApiClient(http);
        var window = new MainWindow(ClientSettings.Create("https://test.invalid"), api, new SystemCaptureService(), new Company { Id = 42, CustomerNumber = "10001", Name = "Test" });
        try
        {
            foreach (var name in new[] { "SendHostnameCheckBox", "SendManufacturerCheckBox", "SendOperatingSystemCheckBox", "SendArticleNumberCheckBox", "SendManufacturerNumberCheckBox", "SendSerialNumberCheckBox", "SendTeamViewerIdCheckBox", "SendRemarkCheckBox", "SendInternalRemarkCheckBox", "SendPurchaseDateCheckBox", "SendGuaranteeMonthCheckBox", "SendGuaranteeExpireCheckBox", "SendGuaranteeRemarkCheckBox", "AttachInvoiceCheckBox" })
                Control<CheckBox>(window, name).IsChecked = false;
            Control<TextBox>(window, "TransferHostnameTextBox").Text = "DEMO-SERVER-01";
            Control<TextBox>(window, "TransferSerialNumberTextBox").Text = "DEMO0002";
            ((DispatcherTimer)typeof(MainWindow).GetField("_lookupTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Stop();
            Control<TextBox>(window, "TransferModelTextBox").Text = "Testmodell";
            Control<TextBox>(window, "TransferRemarkTextBox").Text = "Nicht senden";
            Control<TextBox>(window, "TransferManufacturerNumberTextBox").Text = "123";
            var selection = (TransferPreviewRequest)Method("BuildTransferPreviewRequest").Invoke(window, null)!;
            Require(selection.Model == "Testmodell" && selection.Name is null && selection.SerialNumber is null && selection.Server == false && selection.ManufacturerNumber is null && selection.Remark is null && selection.GuaranteeMonth is null && selection.HostId is null && selection.NetworkAdapters.Count == 0, "Unchecked fields leak into export");
            Require(Control<TextBox>(window, "TransferSerialNumberTextBox").IsEnabled && Control<TextBox>(window, "TransferHostnameTextBox").IsEnabled, "Lookup fields disabled");
            var resolved = await (Task<TransferPreviewRequest>)Method("ResolvePreviewActionAsync").Invoke(window, new object[] { selection })!;
            Require(resolved.WriteMode == "SUPPLEMENT" && resolved.TargetDeviceId == 9001 && resolved.SerialNumber is null && resolved.Name is null, "Existing target treated as creation");
            Require(window.FindName("SendServerCheckBox") is null, "Duplicate server checkbox remains");
            Control<CheckBox>(window, "IsServerCheckBox").IsChecked = true;
            selection = (TransferPreviewRequest)Method("BuildTransferPreviewRequest").Invoke(window, null)!;
            Require(selection.Server == true, "Server checkbox lost");
            Control<CheckBox>(window, "IsServerCheckBox").IsChecked = false;
            selection = (TransferPreviewRequest)Method("BuildTransferPreviewRequest").Invoke(window, null)!;
            Require(selection.Server == false, "Explicit false lost");
            policy = 1;
            try { await api.ResolveDeviceTargetAsync(42, selection, default); throw new Exception("Old server accepted"); }
            catch (InvalidOperationException error) when (error.Message.Contains("Serverupdate")) { }
            policy = 2; foreign = true;
            try { await api.ResolveDeviceTargetAsync(42, selection, default); throw new Exception("Foreign target accepted"); }
            catch (ArgumentException error) when (error.Message.Contains("anderen Kunden")) { }
            Console.WriteLine("Client optional fields: omitted values, separate lookup, existing target, foreign block and server policy: OK");
        }
        finally { window.Close(); }
    }
    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static T Control<T>(MainWindow window, string name) where T : class => window.FindName(name) as T ?? throw new Exception("Missing control " + name);
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request); }
}
