using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using TanssSystemCapture.Client.Models;

namespace TanssSystemCapture.Client;
public partial class MainWindow
{
    private InvoiceSelection _invoiceSelection = new();
    private string? _manualInvoicePath;
    private string? _manualInvoiceFilename;
    private bool _manualInvoiceAwaitingContext;
    private InvoiceLookup? _invoiceLookup;
    private bool _invoiceLookupBusy;
    private bool _invoiceSelectionBusy;
    private int _invoiceSelectionRevision;
    private string? _invoiceEventId;
    private readonly List<string> _temporaryInvoicePaths = new();
    private readonly System.Windows.Threading.DispatcherTimer _invoiceStatusTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _invoiceStatusBusy;

    private void ClearInvoiceSelection()
    {
        _invoiceSelectionRevision++;
        _invoiceSelection = new(); _manualInvoicePath = null; _invoiceLookup = null;
        _manualInvoiceFilename = null; _manualInvoiceAwaitingContext = false;
        _invoiceEventId = null; _invoiceStatusTimer.Stop();
        if (!IsInitialized) return;
        InvoiceStatusTextBlock.Text = "PDF-Anhang ausstehend";
        OpenInvoiceButton.IsEnabled = false;
    }
    private void ResetInvoiceForChangedContext()
    {
        // An unassigned local PDF survives the initial customer/serial selection.
        // Once staging starts it belongs to that context and later changes clear it.
        if (!_manualInvoiceAwaitingContext || _manualInvoicePath is null) { ClearInvoiceSelection(); return; }
        _invoiceSelectionRevision++;
        _invoiceLookup = null; _invoiceEventId = null; _invoiceStatusTimer.Stop();
        ShowPendingManualInvoice();
        OpenInvoiceButton.IsEnabled = !_invoiceSelectionBusy;
    }
    private void ShowPendingManualInvoice()
    {
        var missing = _selectedCompany.Id <= 0
            ? "Kunden auswählen"
            : "Seriennummer eingeben";
        InvoiceStatusTextBlock.Text = $"PDF ausgewählt: {_manualInvoiceFilename}\nQuelle: manuell · {missing}";
    }
    private async void LookupInvoiceButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_invoiceSelectionBusy || _isDeviceCreationInProgress) return;
        // Explicit requery selects the mailbox invoice in place of the manual file.
        ClearInvoiceSelection(); InvalidateTransferPreview(); await LookupInvoiceAsync();
    }
    private async Task LookupInvoiceAsync()
    {
        if (_invoiceLookupBusy || _manualInvoicePath is not null) return;
        var serial = TransferSerialNumberTextBox.Text.Trim();
        if (serial.Length < 5) return;
        var revision = _contextRevision;
        var selectionRevision = _invoiceSelectionRevision;
        _invoiceLookupBusy = true; UpdateTransferAvailability();
        try {
            var result = await _apiClient.LookupInvoiceAsync(serial, CancellationToken.None);
            if (revision != _contextRevision || selectionRevision != _invoiceSelectionRevision || _manualInvoicePath is not null) return;
            _invoiceLookup = result;
            InvoiceStatusTextBlock.Text = result.Status switch {
                "unique" => $"Rechnung gefunden: {result.InvoiceNumber} · {result.Filename}\nQuelle: Rechnungspostfach · nach Übertragung vorgemerkt",
                "ambiguous" => "Mehrere Rechnungen gefunden · bitte PDF auswählen",
                "disabled" => "Rechnungsworker nicht aktiviert · PDF-Anhang ausstehend",
                "index_pending" => "Rechnungsindex wird vorbereitet · PDF-Anhang ausstehend",
                _ => "Keine Rechnung gefunden · PDF-Anhang ausstehend"
            };
            OpenInvoiceButton.IsEnabled = result.CanOpen;
        } catch (Exception exception) { if (revision == _contextRevision && selectionRevision == _invoiceSelectionRevision && _manualInvoicePath is null) InvoiceStatusTextBlock.Text = "Rechnungsprüfung nicht verfügbar: " + exception.Message; }
        finally { _invoiceLookupBusy = false; UpdateTransferAvailability(); }
    }
    private async void SelectInvoiceButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_invoiceSelectionBusy || _isDeviceCreationInProgress) return;
        var dialog = new OpenFileDialog { Filter = "PDF-Dateien (*.pdf)|*.pdf", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) == true) await SelectInvoiceAsync(dialog.FileName);
    }
    private async void Invoice_OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length == 1) { e.Handled = true; await SelectInvoiceAsync(files[0]); }
    }
    private async Task SelectInvoiceAsync(string filename)
    {
        if (_invoiceSelectionBusy || _isDeviceCreationInProgress) return;
        var revision = _contextRevision;
        var selectionRevision = ++_invoiceSelectionRevision;
        bool IsCurrent() => revision == _contextRevision && selectionRevision == _invoiceSelectionRevision;
        var selectedLocally = false;
        _invoiceSelectionBusy = true; UpdateTransferAvailability();
        OpenInvoiceButton.IsEnabled = false;
        try {
            if (new FileInfo(filename).Length > 20 * 1024 * 1024) throw new ArgumentException("PDF darf höchstens 20 MiB groß sein.");
            var bytes = await File.ReadAllBytesAsync(filename);
            if (bytes.Length > 20 * 1024 * 1024) throw new ArgumentException("PDF darf höchstens 20 MiB groß sein.");
            if (!bytes.AsSpan().StartsWith("%PDF-"u8)) throw new ArgumentException("Bitte eine gültige PDF-Datei auswählen.");
            if (!IsCurrent()) return;
            // Select an immutable local preview before contacting the server. A failed
            // staging request must never silently restore the mailbox invoice.
            var localCopy = Path.Combine(Path.GetTempPath(), "TANSS_Rechnung_" + Guid.NewGuid().ToString("N") + ".pdf");
            await File.WriteAllBytesAsync(localCopy, bytes); _temporaryInvoicePaths.Add(localCopy);
            if (!IsCurrent()) return;
            _manualInvoicePath = localCopy; _invoiceSelection = new InvoiceSelection { Mode = "MANUAL" };
            _manualInvoiceFilename = Path.GetFileName(filename); _manualInvoiceAwaitingContext = true;
            _invoiceLookup = null; selectedLocally = true;
            _invoiceEventId = null; _invoiceStatusTimer.Stop();
            AttachInvoiceCheckBox.IsChecked = true;
            ShowPendingManualInvoice();
            InvalidateTransferPreview();
        } catch (Exception exception) {
            if (IsCurrent()) InvoiceStatusTextBlock.Text = "PDF-Auswahl fehlgeschlagen: " + exception.Message;
        }
        finally {
            _invoiceSelectionBusy = false;
            OpenInvoiceButton.IsEnabled = _manualInvoicePath is not null || _invoiceLookup?.CanOpen == true;
            UpdateTransferAvailability();
        }
        if (selectedLocally && IsCurrent()) await StagePendingInvoiceAsync();
    }
    private async Task StagePendingInvoiceAsync()
    {
        if (_invoiceSelectionBusy || _isDeviceCreationInProgress || !_manualInvoiceAwaitingContext || _manualInvoicePath is null) return;
        var companyId = _selectedCompany.Id;
        var serial = TransferSerialNumberTextBox.Text.Trim();
        if (companyId <= 0 || serial.Length < 5) { ShowPendingManualInvoice(); return; }
        var revision = _contextRevision;
        var selectionRevision = _invoiceSelectionRevision;
        var path = _manualInvoicePath; var filename = _manualInvoiceFilename!;
        bool IsCurrent() => revision == _contextRevision && selectionRevision == _invoiceSelectionRevision;
        _manualInvoiceAwaitingContext = false;
        _invoiceSelectionBusy = true;
        OpenInvoiceButton.IsEnabled = false;
        InvoiceStatusTextBlock.Text = $"PDF ausgewählt: {filename}\nQuelle: manuell · Vormerkung läuft";
        InvalidateTransferPreview();
        try {
            var bytes = await File.ReadAllBytesAsync(path);
            if (!IsCurrent()) return;
            var result = await _apiClient.StageInvoiceAsync(companyId, serial, filename, bytes, CancellationToken.None);
            if (!IsCurrent()) return;
            if (!Guid.TryParse(result.UploadId, out var uploadId) || uploadId == Guid.Empty)
                throw new InvalidOperationException("Der Importdienst hat keine gültige PDF-Vormerkung bestätigt.");
            _invoiceSelection = new InvoiceSelection { Mode = "MANUAL", UploadId = result.UploadId };
            InvoiceStatusTextBlock.Text = $"PDF ausgewählt: {filename}\nQuelle: manuell · nach Geräteübertragung vorgemerkt";
        } catch (Exception exception) {
            if (IsCurrent()) InvoiceStatusTextBlock.Text = $"PDF ausgewählt: {filename}\nQuelle: manuell · Vormerkung fehlgeschlagen: {exception.Message}\nBitte PDF erneut auswählen oder den PDF-Anhang abwählen.";
        } finally {
            _invoiceSelectionBusy = false;
            OpenInvoiceButton.IsEnabled = _manualInvoicePath is not null || _invoiceLookup?.CanOpen == true;
            UpdateTransferAvailability();
        }
    }
    private async void OpenInvoiceButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_invoiceSelectionBusy) return;
        var revision = _contextRevision;
        var selectionRevision = _invoiceSelectionRevision;
        try {
            var path = await ResolveInvoicePreviewPathAsync();
            if (path is null || revision != _contextRevision || selectionRevision != _invoiceSelectionRevision) return;
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        } catch (Exception exception) { if (revision == _contextRevision && selectionRevision == _invoiceSelectionRevision) InvoiceStatusTextBlock.Text = "PDF konnte nicht geöffnet werden: " + exception.Message; }
    }
    private async Task<string?> ResolveInvoicePreviewPathAsync()
    {
        if (_invoiceSelectionBusy) return null;
        if (_manualInvoicePath is not null) return _manualInvoicePath;
        if (_invoiceLookup?.CanOpen != true) return null;
        var revision = _contextRevision;
        var selectionRevision = _invoiceSelectionRevision;
        var bytes = await _apiClient.DownloadInvoiceAsync(TransferSerialNumberTextBox.Text.Trim(), CancellationToken.None);
        if (revision != _contextRevision || selectionRevision != _invoiceSelectionRevision) return null;
        var path = Path.Combine(Path.GetTempPath(), "TANSS_Rechnung_" + Guid.NewGuid().ToString("N") + ".pdf");
        await File.WriteAllBytesAsync(path, bytes); _temporaryInvoicePaths.Add(path);
        return revision == _contextRevision && selectionRevision == _invoiceSelectionRevision ? path : null;
    }
    private bool IsInvoiceReadyForTransfer => AttachInvoiceCheckBox.IsChecked != true ||
        _invoiceSelection.Mode != "MANUAL" || !string.IsNullOrWhiteSpace(_invoiceSelection.UploadId);
    private void ShowInvoiceQueueResult(DeviceCreateResponse result)
    {
        InvoiceStatusTextBlock.Text = result.InvoiceQueueStatus switch {
            "queued" => "Gerät gespeichert · PDF-Verarbeitung vorgemerkt",
            "not_requested" => "Gerät gespeichert · PDF-Anhang abgewählt",
            "disabled" => "Gerät gespeichert · Rechnungsworker nicht aktiviert",
            "no_serial" => "Gerät gespeichert · PDF-Anhang ausstehend (Seriennummer fehlt)",
            _ => "Gerät gespeichert · PDF-Vormerkung fehlgeschlagen; bitte prüfen"
        };
        _invoiceEventId = result.InvoiceEventId;
        _invoiceStatusTimer.Stop();
        _invoiceStatusTimer.Tick -= InvoiceStatus_OnTick;
        _invoiceStatusTimer.Tick += InvoiceStatus_OnTick;
        if (_invoiceEventId is not null) _invoiceStatusTimer.Start();
    }
    private async void InvoiceStatus_OnTick(object? sender, EventArgs e)
    {
        if (_invoiceStatusBusy || _invoiceEventId is null) return;
        var id = _invoiceEventId; _invoiceStatusBusy = true;
        try {
            var status = await _apiClient.InvoiceStatusAsync(id, CancellationToken.None);
            if (id != _invoiceEventId) return;
            InvoiceStatusTextBlock.Text = status.Status switch {
                "uploaded" => $"Rechnungs-PDF angehängt · TANSS-Dokument-ID {status.DocumentId}",
                "review" => "PDF-Verarbeitung benötigt Prüfung · " + status.Reason,
                _ => "PDF-Anhang ausstehend · Worker prüft die Rechnung"
            };
            if (status.Status is "uploaded" or "review") _invoiceStatusTimer.Stop();
        } catch (Exception exception) { if (id == _invoiceEventId) { InvoiceStatusTextBlock.Text = "PDF-Status nicht verfügbar: " + exception.Message; _invoiceStatusTimer.Stop(); } }
        finally { _invoiceStatusBusy = false; }
    }
}
