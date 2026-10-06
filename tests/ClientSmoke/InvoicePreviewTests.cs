using System.IO;
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

internal static class InvoicePreviewTests
{
    public static async Task RunAsync()
    {
        await RunUnassignedInvoiceAsync();
        var mailboxBytes = Encoding.ASCII.GetBytes("%PDF-1.4\nMailbox A\n%%EOF");
        var manualBytes = Encoding.ASCII.GetBytes("%PDF-1.4\nManual B\n%%EOF");
        var replacementBytes = Encoding.ASCII.GetBytes("%PDF-1.4\nManual C\n%%EOF");
        var uploadId = Guid.NewGuid().ToString();
        Func<Task<HttpResponseMessage>> stage = () => Task.FromResult(Json(new { uploadId, filename = "manual.pdf" }));
        Func<Task<HttpResponseMessage>> lookup = () => Task.FromResult(Json(new { status = "unique", invoiceNumber = "MAIL-A", filename = "mail.pdf", canOpen = true }));
        Func<Task<HttpResponseMessage>> download = () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(mailboxBytes) });
        byte[]? stagedBytes = null;
        var downloads = 0;
        using var handler = new StubHandler(async request => {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/invoices/manual", StringComparison.Ordinal)) {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                stagedBytes = Convert.FromBase64String(body.RootElement.GetProperty("pdfBase64").GetString()!);
                Require(path == "/api/v1/companies/42/invoices/manual" && body.RootElement.GetProperty("serialNumber").GetString() == "DEMO0001", "Manual staging context incorrect");
                return await stage();
            }
            if (path.EndsWith("/invoices/pdf", StringComparison.Ordinal)) { downloads++; return await download(); }
            if (path.EndsWith("/invoices/by-serial-number", StringComparison.Ordinal)) return await lookup();
            throw new InvalidOperationException("Unexpected HTTP request: " + path);
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid/"), Timeout = TimeSpan.FromSeconds(10) };
        using var api = new ImportApiClient(http);
        var settings = ClientSettings.Create("https://test.invalid");
        var window = new MainWindow(settings, api, new SystemCaptureService(), new Company { Id = 42, CustomerNumber = "10001", Name = "Test" });
        var filename = Path.Combine(Path.GetTempPath(), "invoice-selection-test-" + Guid.NewGuid().ToString("N") + ".pdf");
        try {
            Control<TextBox>(window, "TransferSerialNumberTextBox").Text = "DEMO0001";
            Field<DispatcherTimer>(window, "_lookupTimer").Stop();
            Control<TextBox>(window, "TransferModelTextBox").Text = "Test model";
            await InvokeTask(window, "LookupInvoiceAsync");
            var mailboxPath = await Preview(window);
            Require(mailboxPath is not null && File.ReadAllBytes(mailboxPath).SequenceEqual(mailboxBytes), "Mailbox preview incorrect");

            // Delayed staging cannot leave the mailbox PDF openable. The server and
            // preview must receive the same immutable local selection.
            await File.WriteAllBytesAsync(filename, manualBytes);
            var stageStarted = Signal(); var stageReply = Reply();
            stage = () => { stageStarted.TrySetResult(); return stageReply.Task; };
            var selecting = InvokeTask(window, "SelectInvoiceAsync", filename);
            await stageStarted.Task;
            Require(await Preview(window) is null && !Control<Button>(window, "OpenInvoiceButton").IsEnabled, "Old preview available during selection");
            Require(!Control<Button>(window, "CreateTransferPreviewButton").IsEnabled, "Transfer available during staging");
            await File.WriteAllBytesAsync(filename, replacementBytes);
            stageReply.SetResult(Json(new { uploadId, filename = "manual.pdf" }));
            await selecting;
            var manualPath = await Preview(window);
            Require(manualPath is not null && File.ReadAllBytes(manualPath).SequenceEqual(manualBytes), "Selected PDF was replaced by mailbox/source changes");
            Require(stagedBytes is not null && stagedBytes.SequenceEqual(manualBytes), "Staged bytes differ from preview");
            Require(downloads == 1, "Manual preview downloaded mailbox PDF");
            Require(Build(window).InvoiceAttachment?.Mode == "MANUAL" && Build(window).InvoiceAttachment?.UploadId == uploadId, "Manual transfer selection lost");

            // An expired session retains the new local PDF and blocks attachment
            // transfer instead of silently falling back to AUTO.
            stage = () => Task.FromResult(Json(new { title = "Anmeldung erforderlich", detail = "Sitzung abgelaufen" }, HttpStatusCode.Unauthorized));
            await InvokeTask(window, "SelectInvoiceAsync", filename);
            var failedPath = await Preview(window);
            Require(failedPath is not null && File.ReadAllBytes(failedPath).SequenceEqual(replacementBytes), "Failed staging restored mailbox PDF");
            Require(Control<Button>(window, "OpenInvoiceButton").IsEnabled, "Failed staging hid local preview");
            Require(Control<TextBlock>(window, "InvoiceStatusTextBlock").Text.Contains("Vormerkung fehlgeschlagen", StringComparison.Ordinal), "Staging error not displayed");
            Require(!Control<Button>(window, "CreateDeviceButton").IsEnabled && !Control<Button>(window, "CreateTransferPreviewButton").IsEnabled, "Unstaged attachment allowed");
            ExpectBlocked(window);
            Control<CheckBox>(window, "AttachInvoiceCheckBox").IsChecked = false;
            Require(Build(window).InvoiceAttachment?.Mode == "NONE", "Unchecking PDF did not allow NONE");
            Control<CheckBox>(window, "AttachInvoiceCheckBox").IsChecked = true;
            stage = () => Task.FromResult(Json(new { uploadId = "", filename = "manual.pdf" }));
            await InvokeTask(window, "SelectInvoiceAsync", filename);
            ExpectBlocked(window);

            // A mailbox download already in flight cannot open after switching to
            // a manual source, even though customer and serial stay unchanged.
            stage = () => Task.FromResult(Json(new { uploadId, filename = "manual.pdf" }));
            Invoke(window, "ClearInvoiceSelection"); await InvokeTask(window, "LookupInvoiceAsync");
            var downloadStarted = Signal(); var downloadReply = Reply();
            download = () => { downloadStarted.TrySetResult(); return downloadReply.Task; };
            var stalePreview = Preview(window);
            await downloadStarted.Task;
            await InvokeTask(window, "SelectInvoiceAsync", filename);
            downloadReply.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(mailboxBytes) });
            Require(await stalePreview is null, "Old mailbox download survived source switch");
            Require(File.ReadAllBytes((await Preview(window))!).SequenceEqual(replacementBytes), "Source switch preview incorrect");

            // A late failed lookup must not overwrite manual selection status.
            Invoke(window, "ClearInvoiceSelection");
            var lookupStarted = Signal(); var lookupReply = Reply();
            lookup = () => { lookupStarted.TrySetResult(); return lookupReply.Task; };
            var staleLookup = InvokeTask(window, "LookupInvoiceAsync"); await lookupStarted.Task;
            await InvokeTask(window, "SelectInvoiceAsync", filename);
            var manualStatus = Control<TextBlock>(window, "InvoiceStatusTextBlock").Text;
            lookupReply.SetResult(Json(new { title = "Unavailable" }, HttpStatusCode.ServiceUnavailable)); await staleLookup;
            Require(Control<TextBlock>(window, "InvoiceStatusTextBlock").Text == manualStatus, "Late lookup error overwrote manual status");

            // Customer/serial changes invalidate in-flight manual staging.
            stageStarted = Signal(); stageReply = Reply();
            stage = () => { stageStarted.TrySetResult(); return stageReply.Task; };
            selecting = InvokeTask(window, "SelectInvoiceAsync", filename); await stageStarted.Task;
            Control<TextBox>(window, "TransferSerialNumberTextBox").Text = "DEMO0003";
            Field<DispatcherTimer>(window, "_lookupTimer").Stop();
            stageReply.SetResult(Json(new { uploadId, filename = "manual.pdf" })); await selecting;
            Require(Field<InvoiceSelection>(window, "_invoiceSelection").Mode == "AUTO" && await Preview(window) is null, "Stale manual selection survived context change");

            // Explicit requery intentionally returns to mailbox selection.
            lookup = () => Task.FromResult(Json(new { status = "unique", canOpen = true }));
            download = () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(mailboxBytes) });
            await InvokeTask(window, "LookupInvoiceAsync");
            Require(File.ReadAllBytes((await Preview(window))!).SequenceEqual(mailboxBytes), "Explicit AUTO lookup did not restore mailbox preview");
            Console.WriteLine("Invoice preview regressions: manual PDF, failed/invalid staging, transfer guard, late lookup/download, context change and AUTO requery: OK");
        } finally { window.Close(); File.Delete(filename); }
    }
    private static async Task RunUnassignedInvoiceAsync()
    {
        var mailboxBytes = Encoding.ASCII.GetBytes("%PDF-1.4\nUnassigned mailbox\n%%EOF");
        var localBytes = Encoding.ASCII.GetBytes("%PDF-1.4\nUnassigned manual\n%%EOF");
        var uploadId = Guid.NewGuid().ToString();
        var stages = 0; var downloads = 0; var rejectCustomer = false;
        using var handler = new StubHandler(async request => {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/companies/by-customer-number/10001", StringComparison.Ordinal))
                return rejectCustomer ? Json(new { title = "Kunde nicht gefunden" }, HttpStatusCode.BadRequest)
                    : Json(new { company = new { id = 42, customerNumber = "10001", name = "Test" } });
            if (path.EndsWith("/invoices/manual", StringComparison.Ordinal)) {
                stages++;
                Require(path == "/api/v1/companies/42/invoices/manual", "Unassigned PDF staged for wrong customer");
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Require(body.RootElement.GetProperty("serialNumber").GetString() == "DEMO0001", "Unassigned PDF staged for wrong serial");
                Require(Convert.FromBase64String(body.RootElement.GetProperty("pdfBase64").GetString()!).SequenceEqual(localBytes), "Deferred staging used wrong PDF bytes");
                return Json(new { uploadId, filename = "local.pdf" });
            }
            if (path.EndsWith("/invoices/by-serial-number", StringComparison.Ordinal)) return Json(new { status = "unique", canOpen = true });
            if (path.EndsWith("/invoices/pdf", StringComparison.Ordinal)) {
                downloads++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(mailboxBytes) };
            }
            throw new InvalidOperationException("Unexpected unassigned invoice request: " + path);
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid/"), Timeout = TimeSpan.FromSeconds(10) };
        using var api = new ImportApiClient(http);
        var window = new MainWindow(ClientSettings.Create("https://test.invalid"), api, new SystemCaptureService());
        var filename = Path.Combine(Path.GetTempPath(), "unassigned-invoice-" + Guid.NewGuid().ToString("N") + ".pdf");
        try {
            await File.WriteAllBytesAsync(filename, localBytes);
            Control<TextBox>(window, "TransferModelTextBox").Text = "Test model";
            Control<TextBox>(window, "TransferSerialNumberTextBox").Text = "DEMO0001";
            Field<DispatcherTimer>(window, "_lookupTimer").Stop();
            await InvokeTask(window, "LookupInvoiceAsync");
            Require(File.ReadAllBytes((await Preview(window))!).SequenceEqual(mailboxBytes), "No-customer mailbox preview incorrect");
            await InvokeTask(window, "SelectInvoiceAsync", filename);
            var path = await Preview(window);
            Require(path is not null && File.ReadAllBytes(path).SequenceEqual(localBytes), "No-customer selection still opened mailbox PDF");
            Require(downloads == 1 && stages == 0, "No-customer selection contacted staging/mail PDF endpoint");
            Require(Control<Button>(window, "OpenInvoiceButton").IsEnabled, "No-customer local preview disabled");
            ExpectBlocked(window);

            // Failed customer resolution must retain the unassigned local file.
            Control<TextBox>(window, "CustomerNumberTextBox").Text = "10001";
            rejectCustomer = true;
            await InvokeTask(window, "ChangeCustomerAsync");
            Require(await Preview(window) == path && stages == 0, "Failed customer search discarded/staged local PDF");
            rejectCustomer = false;
            await InvokeTask(window, "ChangeCustomerAsync");
            Require(await Preview(window) == path && stages == 1, "Initial customer selection did not retain/stage local PDF");
            Require(Build(window).InvoiceAttachment?.Mode == "MANUAL" && Build(window).InvoiceAttachment?.UploadId == uploadId, "Deferred attachment selection not transferable");

            // A bound PDF is cleared by later customer changes, preventing reuse
            // of the old customer-scoped upload ID.
            await InvokeTask(window, "ChangeCustomerAsync");
            Require(await Preview(window) is null && stages == 1, "Bound PDF survived customer change");

            // Selecting a PDF before both customer and serial also works. It is
            // staged exactly once after both become available through the UI flow.
            typeof(MainWindow).GetField("_selectedCompany", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, new Company());
            Control<TextBox>(window, "TransferSerialNumberTextBox").Text = "";
            Field<DispatcherTimer>(window, "_lookupTimer").Stop();
            await InvokeTask(window, "SelectInvoiceAsync", filename);
            path = await Preview(window);
            Require(path is not null && stages == 1, "Empty-context selection failed");
            await InvokeTask(window, "ChangeCustomerAsync");
            Require(await Preview(window) == path && stages == 1, "PDF staged before serial was entered");
            Control<TextBox>(window, "TransferSerialNumberTextBox").Text = "DEMO0001";
            Field<DispatcherTimer>(window, "_lookupTimer").Stop();
            await InvokeTask(window, "RefreshLookupsAsync");
            Require(await Preview(window) == path && stages == 2, "First serial selection discarded/unnecessarily restaged PDF");
            await InvokeTask(window, "RefreshLookupsAsync");
            Require(stages == 2, "Repeated lookup restaged bound PDF");
            Console.WriteLine("Unassigned invoice regressions: no-customer preview, failed/first customer selection, missing serial, deferred exact-byte staging and bound context invalidation: OK");
        } finally { window.Close(); File.Delete(filename); }
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<HttpResponseMessage> Reply() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static T Control<T>(MainWindow window, string name) => (T)window.FindName(name);
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static object? Invoke(MainWindow window, string method, params object[] args) => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    private static Task InvokeTask(MainWindow window, string method, params object[] args) => (Task)Invoke(window, method, args)!;
    private static Task<string?> Preview(MainWindow window) => (Task<string?>)Invoke(window, "ResolveInvoicePreviewPathAsync")!;
    private static TransferPreviewRequest Build(MainWindow window) => (TransferPreviewRequest)Invoke(window, "BuildTransferPreviewRequest")!;
    private static void ExpectBlocked(MainWindow window) {
        try { Build(window); throw new InvalidOperationException("Unstaged PDF was accepted"); }
        catch (TargetInvocationException error) when (error.InnerException is ArgumentException) { }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
