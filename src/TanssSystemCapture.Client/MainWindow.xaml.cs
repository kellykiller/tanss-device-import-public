using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using TanssSystemCapture.Client.Models;
using TanssSystemCapture.Client.Services;

namespace TanssSystemCapture.Client;

public partial class MainWindow : Window
{
    private ClientSettings _settings;
    private ImportApiClient _apiClient;
    private readonly SystemCaptureService _systemCaptureService;
    private readonly ObservableCollection<NetworkAdapterTransferItem> _adapterTransferItems = new();
    private Company _selectedCompany;
    private SystemCaptureResult? _captureResult;
    private WortmannWarranty? _detectedWortmannWarranty;
    private bool _isModelLookupInProgress;
    private bool _isApplyingModelValue;
    private string _capturedModelFallback = string.Empty;
    private string? _automaticallyAppliedModel;
    private string? _automaticallyAppliedModelSerial;
    private Host? _validatedVmHost;
    private bool _isHostValidationInProgress;
    private bool _isSerialNumberValidationInProgress;
    private bool _isSapLookupInProgress;
    private bool _isApplyingSapLookupResult;
    private bool _isWarrantyLookupInProgress;
    private bool _isTransferPreviewInProgress;
    private bool _isDeviceCreationInProgress;
    private TransferPreviewResponse? _confirmedTransferPreview;
    private TransferPreviewRequest? _confirmedTransferSelection;
    private DeviceCatalog? _deviceCatalog;
    private string? _automaticallyAppliedSapItemCode;
    private bool _serialBlocked;
    private int _contextRevision;
    private readonly System.Windows.Threading.DispatcherTimer _lookupTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };

    private static readonly JsonSerializerOptions PreviewJsonOptions = new()
    {
        WriteIndented = true
    };

    public MainWindow(
        ClientSettings settings,
        ImportApiClient apiClient,
        SystemCaptureService systemCaptureService,
        Company? selectedCompany = null)
    {
        InitializeComponent();
        ThemeManager.Attach(this);

        _settings = settings;
        _apiClient = apiClient;
        _systemCaptureService = systemCaptureService;
        _selectedCompany = selectedCompany ?? new Company();
        _lookupTimer.Tick += async (_, _) => { _lookupTimer.Stop(); await RefreshLookupsAsync(); };

        NetworkAdapterItemsControl.ItemsSource = _adapterTransferItems;
        UpdateCustomerDisplay();
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        await LoadDeviceCatalogAsync();
        await RefreshSystemCaptureAsync();
    }

    private async void ChangeCustomerButton_OnClick(object sender, RoutedEventArgs e)
    {
        await ChangeCustomerAsync();
    }

    private async Task ChangeCustomerAsync()
    {
        if (_isDeviceCreationInProgress || _isTransferPreviewInProgress) return;
        var number = CustomerNumberTextBox.Text.Trim();
        var revision = ++_contextRevision;
        CustomerStatusTextBlock.Text = "Kunde wird gesucht …";
        _selectedCompany = new Company();
        _serialBlocked = true;
        UpdateCustomerDisplay();
        InvalidateVmHostValidation(); InvalidateSerialNumberValidation(); InvalidateTransferPreview();
        ResetInvoiceForChangedContext();
        try {
            var company = await _apiClient.ResolveCompanyAsync(number, CancellationToken.None);
            if (revision != _contextRevision) return;
            _selectedCompany = company;
            UpdateCustomerDisplay();
            CustomerStatusTextBlock.Text = "";
            await RefreshLookupsAsync();
        } catch (Exception exception) { if (revision == _contextRevision) CustomerStatusTextBlock.Text = exception.Message; }
        UpdateTransferAvailability();
    }

    private async Task RefreshLookupsAsync()
    {
        await StagePendingInvoiceAsync();
        if (_captureResult is null || _isDeviceCreationInProgress) return;
        var revision = _contextRevision;
        var tasks = new[] { LookupSapArticleNumberAsync(), LookupWortmannWarrantyAsync(), ValidateSerialNumberAsync(), LookupInvoiceAsync() };
        UpdateTransferAvailability();
        await Task.WhenAll(tasks);
        UpdateTransferAvailability();
        if (revision != _contextRevision) _lookupTimer.Start();
    }

    private void UpdateTransferAvailability()
    {
        if (!IsInitialized) return;
        CreateDeviceButton.IsEnabled = _selectedCompany.Id > 0 && (!_serialBlocked || string.IsNullOrWhiteSpace(TransferSerialNumberTextBox.Text)) && !_isSerialNumberValidationInProgress && !_isDeviceCreationInProgress && !_invoiceSelectionBusy && IsInvoiceReadyForTransfer && !_isWarrantyLookupInProgress && !_isSapLookupInProgress && !_isModelLookupInProgress && !_invoiceLookupBusy;
        CreateTransferPreviewButton.IsEnabled = _selectedCompany.Id > 0 && !_isDeviceCreationInProgress && !_invoiceSelectionBusy && IsInvoiceReadyForTransfer && !_isWarrantyLookupInProgress && !_isSapLookupInProgress && !_isModelLookupInProgress && !_invoiceLookupBusy;
    }

    private async void RefreshCaptureButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RefreshSystemCaptureAsync();
    }

    private void ResetTransferFieldsButton_OnClick(object sender, RoutedEventArgs e)
    {
        ResetTransferFields();
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async Task LoadDeviceCatalogAsync()
    {
        SendManufacturerCheckBox.IsEnabled = false;
        SendOperatingSystemCheckBox.IsEnabled = false;
        DeviceCatalogStatusTextBlock.Foreground = ThemeManager.GetBrush("SecondaryTextBrush");
        DeviceCatalogStatusTextBlock.Text =
            "Hersteller und Betriebssysteme werden aus TANSS geladen.";

        try
        {
            _deviceCatalog = await _apiClient.GetDeviceCatalogAsync(
                CancellationToken.None);
            ManufacturerComboBox.ItemsSource = _deviceCatalog.Manufacturers;
            OperatingSystemComboBox.ItemsSource = _deviceCatalog.OperatingSystems;
            SendManufacturerCheckBox.IsEnabled =
                _deviceCatalog.Manufacturers.Count > 0;
            SendOperatingSystemCheckBox.IsEnabled =
                _deviceCatalog.OperatingSystems.Count > 0;
            DeviceCatalogStatusTextBlock.Foreground = ThemeManager.GetBrush("SuccessTextBrush");
            DeviceCatalogStatusTextBlock.Text =
                $"{_deviceCatalog.Manufacturers.Count} passende Hersteller und " +
                $"{_deviceCatalog.OperatingSystems.Count} unterstützte Betriebssysteme aus TANSS geladen.";
        }
        catch (Exception exception)
        {
            _deviceCatalog = null;
            ManufacturerComboBox.ItemsSource = null;
            OperatingSystemComboBox.ItemsSource = null;
            DeviceCatalogStatusTextBlock.Foreground = ThemeManager.GetBrush("ErrorTextBrush");
            DeviceCatalogStatusTextBlock.Text =
                "Hersteller und Betriebssysteme konnten nicht aus TANSS geladen werden: " +
                exception.Message;
        }
    }

    private async Task RefreshSystemCaptureAsync()
    {
        RefreshCaptureButton.IsEnabled = false;
        CaptureStatusTextBlock.Text = "Systemdaten werden erfasst …";
        CaptureStatusTextBlock.Foreground = ThemeManager.GetBrush("SecondaryTextBrush");
        NoAdaptersTextBlock.Visibility = Visibility.Collapsed;

        try
        {
            _captureResult = await Task.Run(_systemCaptureService.Capture);
            _detectedWortmannWarranty = null;
            CapturedHostnameTextBlock.Text = _captureResult.Hostname;

            RebuildAdapterTransferItems();
            ResetTransferFields();
            await RefreshLookupsAsync();

            if (_adapterTransferItems.Count == 0)
            {
                CaptureStatusTextBlock.Text =
                    "Es wurde kein aktiver Netzwerkadapter mit IPv4- und MAC-Adresse gefunden.";
                CaptureStatusTextBlock.Foreground = ThemeManager.GetBrush("ErrorTextBrush");
                NoAdaptersTextBlock.Visibility = Visibility.Visible;
                return;
            }

            CaptureStatusTextBlock.Text =
                $"{_adapterTransferItems.Count} geeignete(r) Adapter gefunden. " +
                "Der wahrscheinlich relevante Adapter ist vorausgewählt; weitere Adapter können gleichzeitig aktiviert werden.";
            CaptureStatusTextBlock.Foreground = ThemeManager.GetBrush("SuccessTextBrush");
        }
        catch (Exception exception)
        {
            _captureResult = null;
            _detectedWortmannWarranty = null;
            _adapterTransferItems.Clear();
            CapturedHostnameTextBlock.Text = string.Empty;
            ResetTransferFields();
            NoAdaptersTextBlock.Visibility = Visibility.Visible;
            CaptureStatusTextBlock.Text = "Systemerfassung fehlgeschlagen: " + exception.Message;
            CaptureStatusTextBlock.Foreground = ThemeManager.GetBrush("ErrorTextBrush");
        }
        finally
        {
            RefreshCaptureButton.IsEnabled = true;
        }
    }

    private void RebuildAdapterTransferItems()
    {
        _adapterTransferItems.Clear();

        if (_captureResult is null)
        {
            return;
        }

        for (var index = 0; index < _captureResult.NetworkAdapters.Count; index++)
        {
            var transferItem = new NetworkAdapterTransferItem(
                _captureResult.NetworkAdapters[index],
                isSelectedByDefault: index == 0);
            transferItem.PropertyChanged += (_, _) => InvalidateTransferPreview();
            _adapterTransferItems.Add(transferItem);
        }
    }

    private void ResetTransferFields()
    {
        ResetHostnameTransferField();
        ResetModelTransferField();
        ResetDeviceMetadataFields();
        ResetSerialNumberTransferField();
        ResetTeamViewerTransferField();
        ResetFreeTextFields();
        ResetVirtualMachineFields();
        ResetServerAndGuaranteeFields();

        for (var index = 0; index < _adapterTransferItems.Count; index++)
        {
            _adapterTransferItems[index].Reset(isSelectedByDefault: index == 0);
        }

        InvalidateTransferPreview();
    }

    private void ResetHostnameTransferField()
    {
        TransferHostnameTextBox.Text = _captureResult?.Hostname ?? string.Empty;
        SendHostnameCheckBox.IsChecked = !string.IsNullOrWhiteSpace(_captureResult?.Hostname);
    }

    private void ResetModelTransferField()
    {
        var capturedProductName = _captureResult?.SystemProductName?.Trim() ??
                                  string.Empty;
        var hostname = _captureResult?.Hostname?.Trim() ?? string.Empty;
        var useCapturedProductName = IsUsableSystemProductName(capturedProductName);

        _capturedModelFallback = useCapturedProductName
            ? capturedProductName
            : hostname;
        _automaticallyAppliedModel = null;
        _automaticallyAppliedModelSerial = null;
        SetModelValue(_capturedModelFallback);
        SendModelCheckBox.IsChecked = true;
        ModelDetectionTextBlock.Text = useCapturedProductName
            ? "Modell wurde aus Windows-SystemProductName übernommen. Das Feld ist in TANSS verpflichtend."
            : "Windows hat kein brauchbares Systemmodell geliefert. Als TANSS-Pflichtwert wird der Hostname verwendet.";
    }

    private void SetModelValue(string value)
    {
        _isApplyingModelValue = true;
        try
        {
            TransferModelTextBox.Text = value;
        }
        finally
        {
            _isApplyingModelValue = false;
        }
    }

    private void TransferModelTextBox_OnTextChanged(
        object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        if (!_isApplyingModelValue)
        {
            _automaticallyAppliedModel = null;
            _automaticallyAppliedModelSerial = null;
            ModelDetectionTextBlock.Text = "Modell wurde manuell bearbeitet.";
        }

        InvalidateTransferPreview();
    }

    private async void LookupWortmannModelButton_OnClick(object sender, RoutedEventArgs e)
    {
        await LookupWortmannModelAsync();
    }

    private async Task LookupWortmannModelAsync()
    {
        if (_isModelLookupInProgress)
        {
            return;
        }

        var serialNumber = TransferSerialNumberTextBox.Text.Trim();
        if (serialNumber.Length is < 1 or > 200)
        {
            ModelDetectionTextBlock.Text = "Für die Wortmann-Modellbeschreibung bitte eine gültige Seriennummer eingeben.";
            return;
        }

        var modelBeforeLookup = TransferModelTextBox.Text.Trim();
        _isModelLookupInProgress = true;
        UpdateTransferAvailability();
        LookupWortmannModelButton.IsEnabled = false;
        ModelDetectionTextBlock.Text = "Modellbeschreibung wird bei Wortmann abgefragt …";
        try
        {
            var product = await _apiClient.FindWortmannWarrantyAsync(serialNumber, CancellationToken.None);
            ApplyWortmannModel(product, modelBeforeLookup);
        }
        catch (Exception exception) when (exception is ImportApiException or TaskCanceledException or HttpRequestException)
        {
            if (string.Equals(serialNumber, TransferSerialNumberTextBox.Text.Trim(), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(modelBeforeLookup, TransferModelTextBox.Text.Trim(), StringComparison.Ordinal))
            {
                ModelDetectionTextBlock.Text = "Keine Wortmann-Modellbeschreibung verfügbar. Das vorhandene Modell bleibt bearbeitbar; die Abfrage kann wiederholt werden.";
            }
        }
        finally
        {
            _isModelLookupInProgress = false;
            UpdateTransferAvailability();
            LookupWortmannModelButton.IsEnabled = true;
        }
    }

    private void ApplyWortmannModel(WortmannWarranty product, string modelBeforeLookup)
    {
        var currentSerial = TransferSerialNumberTextBox.Text.Trim();
        if (!string.Equals(product.SerialNumber.Trim(), currentSerial, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!WortmannModelPolicy.IsDescription(product.ProductDescription))
        {
            ModelDetectionTextBlock.Text = "Wortmann hat keine verwendbare Modellbeschreibung geliefert. Bitte das Modell prüfen oder manuell eingeben.";
            return;
        }

        var currentModel = TransferModelTextBox.Text.Trim();
        if (!WortmannModelPolicy.CanApply(product.SerialNumber, currentSerial,
                product.ProductDescription, currentModel, modelBeforeLookup,
                _capturedModelFallback, _automaticallyAppliedModel))
        {
            ModelDetectionTextBlock.Text = "Wortmann-Modellbeschreibung gefunden: " +
                product.ProductDescription!.Trim() + ". Die manuelle Modelleingabe bleibt erhalten.";
            return;
        }

        _automaticallyAppliedModel = product.ProductDescription!.Trim();
        _automaticallyAppliedModelSerial = currentSerial;
        SetModelValue(_automaticallyAppliedModel);
        ModelDetectionTextBlock.Text = "Modellbeschreibung wurde passend zur Seriennummer von Wortmann übernommen.";
    }

    private void InvalidateWortmannModelForChangedSerial()
    {
        if (_automaticallyAppliedModelSerial is null ||
            string.Equals(_automaticallyAppliedModelSerial,
                TransferSerialNumberTextBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(TransferModelTextBox.Text.Trim(), _automaticallyAppliedModel, StringComparison.Ordinal))
        {
            SetModelValue(_capturedModelFallback);
            ModelDetectionTextBlock.Text = "Seriennummer geändert. Bitte die Modellbeschreibung erneut bei Wortmann abfragen.";
        }

        _automaticallyAppliedModel = null;
        _automaticallyAppliedModelSerial = null;
    }

    private static bool IsUsableSystemProductName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim().ToUpperInvariant() switch
        {
            "SYSTEM PRODUCT NAME" => false,
            "DEFAULT SYSTEM PRODUCT NAME" => false,
            "TO BE FILLED BY O.E.M." => false,
            "TO BE FILLED BY OEM" => false,
            "DEFAULT STRING" => false,
            "NOT SPECIFIED" => false,
            "NOT APPLICABLE" => false,
            "UNKNOWN" => false,
            "NONE" => false,
            "N/A" => false,
            "OEM" => false,
            _ => true
        };
    }

    private void ResetDeviceMetadataFields()
    {
        ManufacturerComboBox.SelectedItem = null;
        SendManufacturerCheckBox.IsChecked = false;
        OperatingSystemComboBox.SelectedItem = null;
        SendOperatingSystemCheckBox.IsChecked = false;
        ClearSapArticleNumber();
        HideSapArticleNumberStatus();

        if (_deviceCatalog is null || _captureResult is null)
        {
            return;
        }

        var detectedManufacturer = DeviceCatalogMatcher.FindManufacturer(
            _captureResult.SystemManufacturer,
            _deviceCatalog.Manufacturers);

        if (detectedManufacturer is not null)
        {
            ManufacturerComboBox.SelectedItem = detectedManufacturer;
            SendManufacturerCheckBox.IsChecked = true;
        }

        var detectedOperatingSystem = DeviceCatalogMatcher.FindOperatingSystem(
            _captureResult.OperatingSystemName,
            _deviceCatalog.OperatingSystems);

        if (detectedOperatingSystem is not null)
        {
            OperatingSystemComboBox.SelectedItem = detectedOperatingSystem;
            SendOperatingSystemCheckBox.IsChecked = true;
        }
    }

    private void ResetTeamViewerTransferField()
    {
        TransferTeamViewerIdTextBox.Text = _captureResult?.TeamViewerId ?? string.Empty;
        SendTeamViewerIdCheckBox.IsChecked =
            !string.IsNullOrWhiteSpace(_captureResult?.TeamViewerId);
        TeamViewerDetectionTextBlock.Text =
            _captureResult?.TeamViewerDetectionMessage ??
            "TeamViewer-ID ist nicht verfügbar. Eine manuelle Eingabe ist möglich.";
    }

    private void ResetSerialNumberTransferField()
    {
        TransferSerialNumberTextBox.Text = _captureResult?.SerialNumber ?? string.Empty;
        SendSerialNumberCheckBox.IsChecked =
            !string.IsNullOrWhiteSpace(_captureResult?.SerialNumber);
        SerialNumberDetectionTextBlock.Text =
            _captureResult?.SerialNumberDetectionMessage ??
            "Seriennummer ist nicht verfügbar. Eine manuelle Eingabe ist möglich.";
        InvalidateSerialNumberValidation();
    }

    private void ResetFreeTextFields()
    {
        TransferRemarkTextBox.Text = string.Empty;
        SendRemarkCheckBox.IsChecked = false;
        TransferInternalRemarkTextBox.Text = string.Empty;
        SendInternalRemarkCheckBox.IsChecked = false;
    }

    private void ResetServerAndGuaranteeFields()
    {
        IsServerCheckBox.IsChecked = false;

        if (_detectedWortmannWarranty is null)
        {
            TransferPurchaseDateTextBox.Text = string.Empty;
            SendPurchaseDateCheckBox.IsChecked = false;
            TransferGuaranteeMonthTextBox.Text = string.Empty;
            SendGuaranteeMonthCheckBox.IsChecked = false;
            TransferGuaranteeExpireTextBox.Text = string.Empty;
            SendGuaranteeExpireCheckBox.IsChecked = false;
            TransferGuaranteeRemarkTextBox.Text = string.Empty;
            SendGuaranteeRemarkCheckBox.IsChecked = false;
            WortmannWarrantyStatusTextBlock.Text = string.Empty;
            WortmannWarrantyStatusTextBlock.Visibility = Visibility.Collapsed;
            return;
        }

        ApplyDetectedWortmannWarranty(_detectedWortmannWarranty);
    }

    private async void LookupWortmannWarrantyButton_OnClick(object sender, RoutedEventArgs e) => await LookupWortmannWarrantyAsync();

    private async Task LookupWortmannWarrantyAsync()
    {
        if (_isWarrantyLookupInProgress)
        {
            return;
        }

        var serialNumber = TransferSerialNumberTextBox.Text.Trim();

        if (serialNumber.Length is < 1 or > 200)
        {
            ShowWortmannWarrantyStatus(
                "Bitte zuerst eine gültige Seriennummer mit 1 bis 200 Zeichen eingeben.",
                ThemeManager.GetBrush("ErrorTextBrush"));
            return;
        }

        var revision = _contextRevision;
        var previousWarranty = _detectedWortmannWarranty;
        bool IsCurrent() => revision == _contextRevision && string.Equals(serialNumber, TransferSerialNumberTextBox.Text.Trim(), StringComparison.OrdinalIgnoreCase);
        _isWarrantyLookupInProgress = true;
        UpdateTransferAvailability();
        var modelBeforeLookup = TransferModelTextBox.Text.Trim();
        var manufacturerBeforeLookup = TransferManufacturerNumberTextBox.Text;
        var warrantyInputs = new[] { TransferPurchaseDateTextBox.Text, TransferGuaranteeMonthTextBox.Text, TransferGuaranteeExpireTextBox.Text, TransferGuaranteeRemarkTextBox.Text };
        LookupWortmannWarrantyButton.IsHitTestVisible = false;
        Cursor = System.Windows.Input.Cursors.Wait;
        ShowWortmannWarrantyStatus(
            "Die Seriennummer wird an die offizielle Wortmann-Seriennummernsuche übertragen.",
            ThemeManager.GetBrush("PrimaryTextBrush"));

        try
        {
            var warranty = await _apiClient.FindWortmannWarrantyAsync(
                serialNumber,
                CancellationToken.None);

            if (!IsCurrent())
            {
                ShowWortmannWarrantyStatus("Seriennummer geändert. Bitte Wortmann erneut abfragen.",
                    ThemeManager.GetBrush("WarningTextBrush"));
                return;
            }

            _detectedWortmannWarranty = warranty;
            var boxes = new[] { TransferPurchaseDateTextBox, TransferGuaranteeMonthTextBox, TransferGuaranteeExpireTextBox, TransferGuaranteeRemarkTextBox };
            var checks = new[] { SendPurchaseDateCheckBox, SendGuaranteeMonthCheckBox, SendGuaranteeExpireCheckBox, SendGuaranteeRemarkCheckBox };
            var values = new[] { warranty.ServiceStart, warranty.GuaranteeMonth.ToString(CultureInfo.InvariantCulture), warranty.ServiceEnd, warranty.ServiceDescription };
            var previous = new[] { previousWarranty?.ServiceStart, previousWarranty?.GuaranteeMonth.ToString(CultureInfo.InvariantCulture), previousWarranty?.ServiceEnd, previousWarranty?.ServiceDescription };
            for (var i = 0; i < boxes.Length; i++)
                if (boxes[i].Text == warrantyInputs[i] && (warrantyInputs[i].Length == 0 || warrantyInputs[i] == previous[i])) { boxes[i].Text = values[i]; checks[i].IsChecked = true; }
            if (TransferManufacturerNumberTextBox.Text == manufacturerBeforeLookup && (manufacturerBeforeLookup.Length == 0 || manufacturerBeforeLookup == previousWarranty?.ArticleNumber)) TransferManufacturerNumberTextBox.Text = warranty.ArticleNumber ?? "";
            ApplyWortmannModel(warranty, modelBeforeLookup);

            var serviceCode = string.IsNullOrWhiteSpace(warranty.ServiceCode)
                ? string.Empty
                : $" (Servicecode {warranty.ServiceCode})";
            var manufacturerNumber = string.IsNullOrWhiteSpace(warranty.ArticleNumber)
                ? string.Empty
                : $" Hersteller-Nr. {warranty.ArticleNumber} wird automatisch mit übertragen.";
            ShowWortmannWarrantyStatus(
                $"Wortmann-Garantie erkannt: {warranty.ServiceStart} bis " +
                $"{warranty.ServiceEnd}, {warranty.GuaranteeMonth} Monate{serviceCode}. " +
                manufacturerNumber +
                " Die Garantiewerte können vor der Vorschau geändert oder einzeln abgewählt werden.",
                ThemeManager.GetBrush("SuccessTextBrush"));
        }
        catch (ImportApiException exception)
        {
            if (!IsCurrent()) return;
            ShowWortmannWarrantyStatus(
                $"{exception.Title}: {exception.Message} Die übrige Systemerfassung bleibt nutzbar.",
                ThemeManager.GetBrush("WarningTextBrush"));
        }
        catch (TaskCanceledException)
        {
            if (!IsCurrent()) return;
            ShowWortmannWarrantyStatus(
                "Zeitüberschreitung bei der Wortmann-Abfrage. Die übrige Systemerfassung bleibt nutzbar.",
                ThemeManager.GetBrush("WarningTextBrush"));
        }
        catch (HttpRequestException exception)
        {
            if (!IsCurrent()) return;
            ShowWortmannWarrantyStatus(
                "Der Importdienst ist für die Wortmann-Abfrage nicht erreichbar. " +
                exception.Message,
                ThemeManager.GetBrush("WarningTextBrush"));
        }
        catch (Exception exception)
        {
            if (!IsCurrent()) return;
            ShowWortmannWarrantyStatus(
                "Unerwarteter Fehler bei der Wortmann-Abfrage: " + exception.Message,
                ThemeManager.GetBrush("WarningTextBrush"));
        }
        finally
        {
            if (revision != _contextRevision) _lookupTimer.Start();
            _isWarrantyLookupInProgress = false;
            UpdateTransferAvailability();
            LookupWortmannWarrantyButton.IsHitTestVisible = true;
            Cursor = null;
        }
    }

    private void ShowWortmannWarrantyStatus(string message, Brush color)
    {
        WortmannWarrantyStatusTextBlock.Foreground = color;
        WortmannWarrantyStatusTextBlock.Text = message;
        WortmannWarrantyStatusTextBlock.Visibility = Visibility.Visible;
    }

    private void ApplyDetectedWortmannWarranty(WortmannWarranty warranty)
    {
        TransferPurchaseDateTextBox.Text = warranty.ServiceStart;
        SendPurchaseDateCheckBox.IsChecked = true;
        TransferGuaranteeMonthTextBox.Text =
            warranty.GuaranteeMonth.ToString(CultureInfo.InvariantCulture);
        SendGuaranteeMonthCheckBox.IsChecked = true;
        TransferGuaranteeExpireTextBox.Text = warranty.ServiceEnd;
        SendGuaranteeExpireCheckBox.IsChecked = true;
        TransferGuaranteeRemarkTextBox.Text = warranty.ServiceDescription;
        SendGuaranteeRemarkCheckBox.IsChecked = true;
    }

    private async void ValidateSerialNumberButton_OnClick(object sender, RoutedEventArgs e) => await ValidateSerialNumberAsync();

    private async Task ValidateSerialNumberAsync()
    {
        if (_selectedCompany.Id <= 0) { UpdateTransferAvailability(); return; }
        if (_isSerialNumberValidationInProgress)
        {
            return;
        }

        var serialNumber = TransferSerialNumberTextBox.Text.Trim();

        if (serialNumber.Length == 0) {
            _serialBlocked = false; UpdateTransferAvailability();
            SerialNumberValidationStatusTextBlock.Text = "Seriennummer nicht angegeben; die Zuordnung wird bei der Übertragung über den Hostnamen geprüft.";
            return;
        }
        if (serialNumber.Length > 200)
        {
            ShowSerialNumberValidationError(
                "Bitte eine Seriennummer mit 1 bis 200 Zeichen eingeben.");
            return;
        }

        var companyId = _selectedCompany.Id;
        var revision = _contextRevision;
        _isSerialNumberValidationInProgress = true;
        UpdateTransferAvailability();
        ValidateSerialNumberButton.IsHitTestVisible = false;
        Cursor = System.Windows.Input.Cursors.Wait;
        SerialNumberValidationStatusTextBlock.Visibility = Visibility.Visible;
        SerialNumberValidationStatusTextBlock.Foreground = ThemeManager.GetBrush("PrimaryTextBrush");
        SerialNumberValidationStatusTextBlock.Text =
            "Seriennummer wird TANSS-weit geprüft. " + GetAuthenticationActionHint();

        try
        {
            var matches = await _apiClient.FindDevicesBySerialNumberAsync(
                _selectedCompany.Id,
                serialNumber,
                CancellationToken.None);

            if (companyId != _selectedCompany.Id || revision != _contextRevision || !string.Equals(serialNumber, TransferSerialNumberTextBox.Text.Trim(), StringComparison.OrdinalIgnoreCase)) return;
            _serialBlocked = matches.Any(match => !match.BelongsToSelectedCompany) || matches.Count > 1;
            UpdateTransferAvailability();
            if (matches.Count == 0)
            {
                SerialNumberValidationStatusTextBlock.Foreground = ThemeManager.GetBrush("SuccessTextBrush");
                SerialNumberValidationStatusTextBlock.Text =
                    "Seriennummer geprüft: In TANSS wurde kein vorhandener PC oder Server mit dieser Seriennummer gefunden.";
                return;
            }

            var orderedMatches = matches
                .OrderBy(match => match.BelongsToSelectedCompany)
                .ThenBy(match => match.Company!.CustomerNumber, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(match => match.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var matchLines = orderedMatches.Select(FormatSerialNumberMatch);
            var hasOtherCustomer = orderedMatches.Any(
                match => !match.BelongsToSelectedCompany);

            SerialNumberValidationStatusTextBlock.Foreground = hasOtherCustomer
                ? ThemeManager.GetBrush("ErrorTextBrush")
                : ThemeManager.GetBrush("WarningTextBrush");
            SerialNumberValidationStatusTextBlock.Text =
                $"Seriennummer bereits in TANSS vorhanden: {orderedMatches.Length} Zuordnung(en) gefunden:" +
                Environment.NewLine +
                string.Join(Environment.NewLine, matchLines) + Environment.NewLine + (_serialBlocked ? "Anlegen und Bearbeiten gesperrt: fremder Kunde oder mehrdeutige Seriennummer." : "System gehört zum ausgewählten Kunden und kann ergänzt oder überschrieben werden.");
        }
        catch (ImportApiException exception)
        {
            ShowSerialNumberValidationError($"{exception.Title}: {exception.Message}");
        }
        catch (TaskCanceledException)
        {
            ShowSerialNumberValidationError(
                "Zeitüberschreitung beim TANSS-weiten Prüfen der Seriennummer.");
        }
        catch (HttpRequestException exception)
        {
            ShowSerialNumberValidationError(
                "Der TANSS-Importdienst ist nicht erreichbar oder die HTTPS-Anmeldung wurde abgewiesen. " +
                exception.Message);
        }
        catch (ArgumentException exception)
        {
            ShowSerialNumberValidationError(exception.Message);
        }
        catch (Exception exception)
        {
            ShowSerialNumberValidationError("Unerwarteter Fehler: " + exception.Message);
        }
        finally
        {
            if (revision != _contextRevision) _lookupTimer.Start();
            _isSerialNumberValidationInProgress = false;
            UpdateTransferAvailability();
            ValidateSerialNumberButton.IsHitTestVisible = true;
            Cursor = null;
        }
    }

    private string FormatSerialNumberMatch(DeviceSerialMatch match)
    {
        var hostType = match.Server switch
        {
            true => "Server",
            false => "PC",
            null => "Typ nicht angegeben"
        };
        var activity = match.Active switch
        {
            true => "aktiv",
            false => "inaktiv",
            null => "Status nicht angegeben"
        };
        var customerRelation = match.BelongsToSelectedCompany
            ? "ausgewählter Kunde"
            : "anderer Kunde";
        var customerNumber = string.IsNullOrWhiteSpace(match.Company!.CustomerNumber)
            ? $"TANSS-ID {match.Company.Id}"
            : match.Company.CustomerNumber;

        return
            $"• TANSS-ID {match.Id} – {match.Name} – {hostType}, {activity} – " +
            $"Kunde {customerNumber} – {match.Company.Name} ({customerRelation})";
    }

    private void SendSerialNumberCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (IsInitialized)
        {
            InvalidateTransferPreview();
            InvalidateSapArticleNumberLookup();
            InvalidateTransferPreview();
        }
    }

    private void TransferSerialNumberTextBox_OnTextChanged(
        object sender,
        System.Windows.Controls.TextChangedEventArgs e)
    {
        if (IsInitialized)
        {
            _contextRevision++;
            ResetInvoiceForChangedContext();
            _lookupTimer.Stop(); _lookupTimer.Start();
            InvalidateWortmannModelForChangedSerial();
            InvalidateWortmannWarrantyForChangedSerial();
            InvalidateSerialNumberValidation();
            InvalidateSapArticleNumberLookup();
            InvalidateTransferPreview();
        }
    }

    private void InvalidateWortmannWarrantyForChangedSerial()
    {
        var warranty = _detectedWortmannWarranty;

        if (warranty is null ||
            string.Equals(
                warranty.SerialNumber.Trim(),
                TransferSerialNumberTextBox.Text.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ClearAutoAppliedWarrantyValue(
            TransferPurchaseDateTextBox,
            SendPurchaseDateCheckBox,
            warranty.ServiceStart);
        ClearAutoAppliedWarrantyValue(
            TransferGuaranteeMonthTextBox,
            SendGuaranteeMonthCheckBox,
            warranty.GuaranteeMonth.ToString(CultureInfo.InvariantCulture));
        ClearAutoAppliedWarrantyValue(
            TransferGuaranteeExpireTextBox,
            SendGuaranteeExpireCheckBox,
            warranty.ServiceEnd);
        ClearAutoAppliedWarrantyValue(
            TransferGuaranteeRemarkTextBox,
            SendGuaranteeRemarkCheckBox,
            warranty.ServiceDescription);

        _detectedWortmannWarranty = null;
        TransferManufacturerNumberTextBox.Text = "";
        ShowWortmannWarrantyStatus(
            "Die Seriennummer wurde geändert. Bitte die Wortmann-Garantiedaten erneut abrufen.",
            ThemeManager.GetBrush("WarningTextBrush"));
    }

    private static void ClearAutoAppliedWarrantyValue(
        TextBox textBox,
        CheckBox checkBox,
        string detectedValue)
    {
        if (!string.Equals(
                textBox.Text.Trim(),
                detectedValue.Trim(),
                StringComparison.Ordinal))
        {
            return;
        }

        textBox.Text = string.Empty;
        checkBox.IsChecked = false;
    }

    private void InvalidateSerialNumberValidation()
    {
        _serialBlocked = true;
        UpdateTransferAvailability();
        SerialNumberValidationStatusTextBlock.Text = string.Empty;
        SerialNumberValidationStatusTextBlock.Visibility = Visibility.Collapsed;
    }

    private void ShowSerialNumberValidationError(string message)
    {
        _serialBlocked = true;
        UpdateTransferAvailability();
        SerialNumberValidationStatusTextBlock.Foreground = ThemeManager.GetBrush("ErrorTextBrush");
        SerialNumberValidationStatusTextBlock.Text = message;
        SerialNumberValidationStatusTextBlock.Visibility = Visibility.Visible;
    }

    private void ResetVirtualMachineFields()
    {
        IsVirtualMachineCheckBox.IsChecked = _captureResult?.IsVirtualMachineLikely == true;
        TransferVmHostIdTextBox.Text = string.Empty;
        InvalidateVmHostValidation();
        InvalidateTransferPreview();
        VirtualMachineDetectionTextBlock.Text =
            _captureResult?.VirtualMachineDetectionMessage ??
            "Automatischer VM-Hinweis ist nicht verfügbar. Die VM-Kennzeichnung bleibt vollständig manuell.";
    }

    private async void ValidateVmHostButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isHostValidationInProgress)
        {
            return;
        }

        if (IsVirtualMachineCheckBox.IsChecked != true)
        {
            ShowVmHostValidationError("Das erfasste System ist nicht als virtuelle Maschine gekennzeichnet.");
            return;
        }


        if (!long.TryParse(
                TransferVmHostIdTextBox.Text.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var hostId) ||
            hostId <= 0)
        {
            ShowVmHostValidationError("Bitte eine gültige positive TANSS-ID des VM-Hosts eingeben.");
            return;
        }

        var checkedCompanyId = _selectedCompany.Id;
        var checkedRevision = _contextRevision;
        _isHostValidationInProgress = true;
        _validatedVmHost = null;
        ValidateVmHostButton.IsHitTestVisible = false;
        Cursor = System.Windows.Input.Cursors.Wait;
        HostValidationStatusTextBlock.Visibility = Visibility.Visible;
        HostValidationStatusTextBlock.Foreground = ThemeManager.GetBrush("PrimaryTextBrush");
        HostValidationStatusTextBlock.Text =
            "Host wird geprüft. " + GetAuthenticationActionHint();

        try
        {
            var host = await _apiClient.ResolveHostAsync(
                _selectedCompany.Id,
                hostId,
                CancellationToken.None);

            if (checkedCompanyId != _selectedCompany.Id || checkedRevision != _contextRevision || TransferVmHostIdTextBox.Text.Trim() != hostId.ToString(CultureInfo.InvariantCulture) || IsVirtualMachineCheckBox.IsChecked != true) return;
            _validatedVmHost = host;
            var hostType = _validatedVmHost.Server switch
            {
                true => "Server",
                false => "PC",
                null => "Typ nicht angegeben"
            };
            var activity = _validatedVmHost.Active switch
            {
                true => "aktiv",
                false => "inaktiv",
                null => "Status nicht angegeben"
            };
            HostValidationStatusTextBlock.Foreground = ThemeManager.GetBrush("SuccessTextBrush");
            HostValidationStatusTextBlock.Text =
                $"Host geprüft: TANSS-ID {_validatedVmHost.Id} – {_validatedVmHost.Name} – " +
                $"{hostType}, {activity} – " +
                $"Kunde {_selectedCompany.CustomerNumber} – {_selectedCompany.Name}.";
        }
        catch (ImportApiException exception)
        {
            ShowVmHostValidationError($"{exception.Title}: {exception.Message}");
        }
        catch (TaskCanceledException)
        {
            ShowVmHostValidationError(
                "Zeitüberschreitung beim Zugriff auf den TANSS-Importdienst.");
        }
        catch (HttpRequestException exception)
        {
            ShowVmHostValidationError(
                "Der TANSS-Importdienst ist nicht erreichbar oder die HTTPS-Anmeldung wurde abgewiesen. " +
                exception.Message);
        }
        catch (ArgumentException exception)
        {
            ShowVmHostValidationError(exception.Message);
        }
        catch (Exception exception)
        {
            ShowVmHostValidationError("Unerwarteter Fehler: " + exception.Message);
        }
        finally
        {
            _isHostValidationInProgress = false;
            ValidateVmHostButton.IsHitTestVisible = true;
            Cursor = null;
        }
    }

    private void IsVirtualMachineCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        if (IsVirtualMachineCheckBox.IsChecked != true)
        {
            }

        InvalidateVmHostValidation();
        InvalidateTransferPreview();
    }

    private void TransferVmHostIdTextBox_OnTextChanged(
        object sender,
        System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        InvalidateVmHostValidation();
        InvalidateTransferPreview();
    }

    private void InvalidateVmHostValidation()
    {
        _validatedVmHost = null;
        HostValidationStatusTextBlock.Text = string.Empty;
        HostValidationStatusTextBlock.Visibility = Visibility.Collapsed;
    }

    private void ShowVmHostValidationError(string message)
    {
        _validatedVmHost = null;
        HostValidationStatusTextBlock.Foreground = ThemeManager.GetBrush("ErrorTextBrush");
        HostValidationStatusTextBlock.Text = message;
        HostValidationStatusTextBlock.Visibility = Visibility.Visible;
    }

    private void TransferSelection_OnChanged(object sender, RoutedEventArgs e)
    {
        if (IsInitialized)
        {
            InvalidateTransferPreview();
        }
    }

    private void TransferSelection_OnTextChanged(
        object sender,
        System.Windows.Controls.TextChangedEventArgs e)
    {
        if (IsInitialized)
        {
            InvalidateTransferPreview();
        }
    }

    private bool _sessionCloseCompleted;
    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _sessionCloseCompleted || !_apiClient.HasSession) return;
        e.Cancel = true;
        await Task.Yield();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await _apiClient.LogoutAsync(timeout.Token); }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or ObjectDisposedException) { }
        _sessionCloseCompleted = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _lookupTimer.Stop();
        _invoiceStatusTimer.Stop();
        foreach (var path in _temporaryInvoicePaths) { try { System.IO.File.Delete(path); } catch (System.IO.IOException) { } catch (UnauthorizedAccessException) { } }
        _apiClient.Dispose();
        base.OnClosed(e);
    }

    private void ShowDeviceCreationError(string message)
    {
        DeviceCreationStatusTextBlock.Foreground = ThemeManager.GetBrush("ErrorTextBrush");
        DeviceCreationStatusTextBlock.Text = message;
        DeviceCreationStatusTextBlock.Visibility = Visibility.Visible;
    }

    private static string GetAuthenticationActionHint() =>
        "Die bestätigte TOTP-Sitzung wird verwendet.";

    private void UpdateCustomerDisplay()
    {
        CustomerTextBlock.Text = _selectedCompany.Id <= 0 ? "Bitte Kunden auswählen" :
            $"{_selectedCompany.CustomerNumber} – {_selectedCompany.Name} " +
            $"(TANSS-ID {_selectedCompany.Id})";
    }
}
