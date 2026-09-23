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
    private bool _synchronizingThemeToggle;

    private static readonly JsonSerializerOptions PreviewJsonOptions = new()
    {
        WriteIndented = true
    };

    public MainWindow(
        ClientSettings settings,
        ImportApiClient apiClient,
        SystemCaptureService systemCaptureService,
        Company selectedCompany)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        ThemeManager.ThemeChanged += ThemeManager_OnThemeChanged;
        SynchronizeThemeToggle(ThemeManager.IsDarkMode);

        _settings = settings;
        _apiClient = apiClient;
        _systemCaptureService = systemCaptureService;
        _selectedCompany = selectedCompany;

        NetworkAdapterItemsControl.ItemsSource = _adapterTransferItems;
        UpdateCustomerDisplay();
    }

    private void ThemeToggleButton_OnToggled(
        object sender,
        RoutedEventArgs e)
    {
        if (!IsInitialized || _synchronizingThemeToggle)
        {
            return;
        }

        ThemeManager.ApplyTheme(ThemeToggleButton.IsChecked == true);
    }

    private void ThemeManager_OnThemeChanged(bool darkMode)
    {
        SynchronizeThemeToggle(darkMode);
    }

    private void SynchronizeThemeToggle(bool darkMode)
    {
        _synchronizingThemeToggle = true;
        ThemeToggleButton.IsChecked = darkMode;
        _synchronizingThemeToggle = false;
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        await LoadDeviceCatalogAsync();
        await RefreshSystemCaptureAsync();
    }

    private void ChangeCustomerButton_OnClick(object sender, RoutedEventArgs e)
    {
        var customerSelectionWindow = new CustomerSelectionWindow(_settings)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        if (customerSelectionWindow.ShowDialog() == true &&
            customerSelectionWindow.SelectedSettings is not null &&
            customerSelectionWindow.SelectedCompany is not null &&
            customerSelectionWindow.SelectedApiClient is not null)
        {
            var previousApiClient = _apiClient;
            _apiClient = customerSelectionWindow.SelectedApiClient;
            _settings = customerSelectionWindow.SelectedSettings!;
            _selectedCompany = customerSelectionWindow.SelectedCompany;
            previousApiClient.Dispose();
            UpdateCustomerDisplay();
            InvalidateVmHostValidation();
            InvalidateSerialNumberValidation();
            InvalidateTransferPreview();
        }
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
            await LookupSapArticleNumberAsync();

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

        TransferModelTextBox.Text = useCapturedProductName
            ? capturedProductName
            : hostname;
        SendModelCheckBox.IsChecked = true;
        ModelDetectionTextBlock.Text = useCapturedProductName
            ? "Modell wurde aus Windows-SystemProductName übernommen. Das Feld ist in TANSS verpflichtend."
            : "Windows hat kein brauchbares Systemmodell geliefert. Als TANSS-Pflichtwert wird der Hostname verwendet.";
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

    private async void LookupSapArticleNumberButton_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        await LookupSapArticleNumberAsync();
    }

    private async Task LookupSapArticleNumberAsync()
    {
        if (_isSapLookupInProgress)
        {
            return;
        }

        var serialNumber = TransferSerialNumberTextBox.Text.Trim();

        if (serialNumber.Length is < 1 or > 36)
        {
            ClearSapArticleNumber();
            ShowSapArticleNumberStatus(
                "Für die SAP-Suche wird eine Seriennummer mit 1 bis 36 Zeichen benötigt.",
                ThemeManager.GetBrush("WarningTextBrush"));
            return;
        }

        _isSapLookupInProgress = true;
        LookupSapArticleNumberButton.IsHitTestVisible = false;
        Cursor = System.Windows.Input.Cursors.Wait;
        ShowSapArticleNumberStatus(
            "SAP-Artikelnummer wird anhand der Seriennummer gesucht. " +
            GetAuthenticationActionHint(),
            ThemeManager.GetBrush("PrimaryTextBrush"));

        try
        {
            var lookup = await _apiClient.FindSapItemsBySerialNumberAsync(
                serialNumber,
                CancellationToken.None);

            if (!string.Equals(
                    TransferSerialNumberTextBox.Text.Trim(),
                    lookup.SerialNumber,
                    StringComparison.Ordinal))
            {
                ClearSapArticleNumber();
                ShowSapArticleNumberStatus(
                    "Die Seriennummer wurde während der SAP-Abfrage geändert. " +
                    "Bitte die Suche erneut starten.",
                    ThemeManager.GetBrush("WarningTextBrush"));
                return;
            }

            if (string.Equals(lookup.Resolution, "NONE", StringComparison.Ordinal))
            {
                ClearSapArticleNumber();
                ShowSapArticleNumberStatus(
                    "In SAP Business One wurde kein Artikel zu dieser Seriennummer gefunden. " +
                    "Die SAP-Artikel-Nr. bleibt leer.",
                    ThemeManager.GetBrush("SecondaryTextBrush"));
                return;
            }

            if (string.Equals(lookup.Resolution, "SINGLE", StringComparison.Ordinal))
            {
                ApplySapItemCode(lookup.ItemCode!);
                ShowSapArticleNumberStatus(
                    $"SAP-Artikel-Nr. {lookup.ItemCode} wurde automatisch übernommen.",
                    ThemeManager.GetBrush("SuccessTextBrush"));
                return;
            }

            Cursor = null;
            var selectionWindow = new SapItemSelectionWindow(
                lookup.SerialNumber,
                lookup.ItemCodes)
            {
                Owner = this
            };
            var dialogResult = selectionWindow.ShowDialog();
            var selectedItemCode = selectionWindow.SelectedItemCode;

            if (dialogResult == true &&
                !string.IsNullOrWhiteSpace(selectedItemCode))
            {
                ApplySapItemCode(selectedItemCode);
                ShowSapArticleNumberStatus(
                    $"SAP-Artikel-Nr. {selectedItemCode} wurde ausgewählt und übernommen.",
                    ThemeManager.GetBrush("SuccessTextBrush"));
                return;
            }

            ClearSapArticleNumber();
            ShowSapArticleNumberStatus(
                "Mehrere SAP-Artikel wurden gefunden, aber es wurde keiner ausgewählt. " +
                "Die SAP-Artikel-Nr. bleibt leer.",
                ThemeManager.GetBrush("WarningTextBrush"));
        }
        catch (ImportApiException exception)
        {
            ClearSapArticleNumber();
            ShowSapArticleNumberStatus(
                $"{exception.Title}: {exception.Message} " +
                "Die übrige Systemerfassung bleibt nutzbar.",
                ThemeManager.GetBrush("WarningTextBrush"));
        }
        catch (TaskCanceledException)
        {
            ClearSapArticleNumber();
            ShowSapArticleNumberStatus(
                "Zeitüberschreitung bei der SAP-Abfrage. " +
                "Die übrige Systemerfassung bleibt nutzbar.",
                ThemeManager.GetBrush("WarningTextBrush"));
        }
        catch (HttpRequestException exception)
        {
            ClearSapArticleNumber();
            ShowSapArticleNumberStatus(
                "Der Importdienst ist für die SAP-Abfrage nicht erreichbar. " +
                exception.Message,
                ThemeManager.GetBrush("WarningTextBrush"));
        }
        catch (ArgumentException exception)
        {
            ClearSapArticleNumber();
            ShowSapArticleNumberStatus(
                exception.Message,
                ThemeManager.GetBrush("WarningTextBrush"));
        }
        catch (Exception exception)
        {
            ClearSapArticleNumber();
            ShowSapArticleNumberStatus(
                "Unerwarteter Fehler bei der SAP-Abfrage: " + exception.Message,
                ThemeManager.GetBrush("WarningTextBrush"));
        }
        finally
        {
            _isSapLookupInProgress = false;
            LookupSapArticleNumberButton.IsHitTestVisible = true;
            Cursor = null;
        }
    }

    private void ApplySapItemCode(string itemCode)
    {
        _isApplyingSapLookupResult = true;

        try
        {
            TransferArticleNumberTextBox.Text = itemCode;
            SendArticleNumberCheckBox.IsChecked = true;
            _automaticallyAppliedSapItemCode = itemCode;
        }
        finally
        {
            _isApplyingSapLookupResult = false;
        }
    }

    private void ClearSapArticleNumber()
    {
        _isApplyingSapLookupResult = true;

        try
        {
            TransferArticleNumberTextBox.Text = string.Empty;
            SendArticleNumberCheckBox.IsChecked = false;
            _automaticallyAppliedSapItemCode = null;
        }
        finally
        {
            _isApplyingSapLookupResult = false;
        }
    }

    private void InvalidateSapArticleNumberLookup()
    {
        if (!string.IsNullOrWhiteSpace(_automaticallyAppliedSapItemCode) &&
            string.Equals(
                TransferArticleNumberTextBox.Text.Trim(),
                _automaticallyAppliedSapItemCode,
                StringComparison.Ordinal))
        {
            ClearSapArticleNumber();
        }

        _automaticallyAppliedSapItemCode = null;
        HideSapArticleNumberStatus();
    }

    private void TransferArticleNumberTextBox_OnTextChanged(
        object sender,
        System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        if (!_isApplyingSapLookupResult)
        {
            _automaticallyAppliedSapItemCode = null;
            HideSapArticleNumberStatus();
        }

        InvalidateTransferPreview();
    }

    private void ShowSapArticleNumberStatus(string message, Brush color)
    {
        SapArticleNumberStatusTextBlock.Foreground = color;
        SapArticleNumberStatusTextBlock.Text = message;
        SapArticleNumberStatusTextBlock.Visibility = Visibility.Visible;
    }

    private void HideSapArticleNumberStatus()
    {
        SapArticleNumberStatusTextBlock.Text = string.Empty;
        SapArticleNumberStatusTextBlock.Visibility = Visibility.Collapsed;
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

    private async void LookupWortmannWarrantyButton_OnClick(
        object sender,
        RoutedEventArgs e)
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

        _isWarrantyLookupInProgress = true;
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

            _detectedWortmannWarranty = warranty;
            ApplyDetectedWortmannWarranty(warranty);

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
            ShowWortmannWarrantyStatus(
                $"{exception.Title}: {exception.Message} Die übrige Systemerfassung bleibt nutzbar.",
                ThemeManager.GetBrush("WarningTextBrush"));
        }
        catch (TaskCanceledException)
        {
            ShowWortmannWarrantyStatus(
                "Zeitüberschreitung bei der Wortmann-Abfrage. Die übrige Systemerfassung bleibt nutzbar.",
                ThemeManager.GetBrush("WarningTextBrush"));
        }
        catch (HttpRequestException exception)
        {
            ShowWortmannWarrantyStatus(
                "Der Importdienst ist für die Wortmann-Abfrage nicht erreichbar. " +
                exception.Message,
                ThemeManager.GetBrush("WarningTextBrush"));
        }
        catch (Exception exception)
        {
            ShowWortmannWarrantyStatus(
                "Unerwarteter Fehler bei der Wortmann-Abfrage: " + exception.Message,
                ThemeManager.GetBrush("WarningTextBrush"));
        }
        finally
        {
            _isWarrantyLookupInProgress = false;
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

    private async void ValidateSerialNumberButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isSerialNumberValidationInProgress)
        {
            return;
        }

        if (SendSerialNumberCheckBox.IsChecked != true)
        {
            ShowSerialNumberValidationError("Die Seriennummer ist nicht aktiviert.");
            return;
        }

        var serialNumber = TransferSerialNumberTextBox.Text.Trim();

        if (serialNumber.Length is < 1 or > 200)
        {
            ShowSerialNumberValidationError(
                "Bitte eine Seriennummer mit 1 bis 200 Zeichen eingeben.");
            return;
        }

        _isSerialNumberValidationInProgress = true;
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
                string.Join(Environment.NewLine, matchLines);
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
            _isSerialNumberValidationInProgress = false;
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

        return
            $"• TANSS-ID {match.Id} – {match.Name} – {hostType}, {activity} – " +
            $"Kunde {match.Company!.CustomerNumber} – {match.Company.Name} ({customerRelation})";
    }

    private void SendSerialNumberCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (IsInitialized)
        {
            InvalidateSerialNumberValidation();
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
        SerialNumberValidationStatusTextBlock.Text = string.Empty;
        SerialNumberValidationStatusTextBlock.Visibility = Visibility.Collapsed;
    }

    private void ShowSerialNumberValidationError(string message)
    {
        SerialNumberValidationStatusTextBlock.Foreground = ThemeManager.GetBrush("ErrorTextBrush");
        SerialNumberValidationStatusTextBlock.Text = message;
        SerialNumberValidationStatusTextBlock.Visibility = Visibility.Visible;
    }

    private void ResetVirtualMachineFields()
    {
        IsVirtualMachineCheckBox.IsChecked = false;
        SendVmHostIdCheckBox.IsChecked = false;
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

        if (SendVmHostIdCheckBox.IsChecked != true)
        {
            ShowVmHostValidationError("Die TANSS-ID des VM-Hosts ist nicht aktiviert.");
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
            SendVmHostIdCheckBox.IsChecked = false;
        }

        InvalidateVmHostValidation();
        InvalidateTransferPreview();
    }

    private void SendVmHostIdCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
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

    private async void CreateTransferPreviewButton_OnClick(object sender, RoutedEventArgs e)
    {
        await PrepareTransferPreviewAsync(showDetails: true);
    }

    private async Task<bool> PrepareTransferPreviewAsync(bool showDetails)
    {
        if (_isTransferPreviewInProgress)
        {
            return false;
        }

        _isTransferPreviewInProgress = true;
        CreateTransferPreviewButton.IsHitTestVisible = false;
        Cursor = System.Windows.Input.Cursors.Wait;
        ClearBlockingFieldHighlights();
        TransferPreviewStatusTextBlock.Visibility = Visibility.Visible;
        TransferPreviewStatusTextBlock.Foreground = ThemeManager.GetBrush("PrimaryTextBrush");
        TransferPreviewStatusTextBlock.Text =
            "Auswahl wird serverseitig geprüft. " + GetAuthenticationActionHint();
        TransferPreviewTextBox.Text = string.Empty;
        TransferPreviewTextBox.Visibility = Visibility.Collapsed;

        try
        {
            var request = BuildTransferPreviewRequest();
            var resolvedRequest = await ResolveExistingDeviceActionAsync(request);

            if (resolvedRequest is null)
            {
                TransferPreviewStatusTextBlock.Foreground = ThemeManager.GetBrush("WarningTextBrush");
                TransferPreviewStatusTextBlock.Text =
                    "Die Übertragung wurde abgebrochen; es wurde nichts gespeichert.";
                return false;
            }

            request = resolvedRequest;
            var preview = await _apiClient.CreateTransferPreviewAsync(
                _selectedCompany.Id,
                request,
                CancellationToken.None);

            var output = new StringBuilder();
            output.AppendLine("Es wurde nichts in TANSS gespeichert.");
            output.AppendLine();
            output.AppendLine(
                $"Kunde: {preview.Company!.CustomerNumber} – {preview.Company.Name} " +
                $"(TANSS-ID {preview.Company.Id})");
            output.AppendLine($"Gewählte Aktion: {preview.WriteAction}");
            output.AppendLine($"Dokumentierte Operation: {preview.DocumentedTanssOperation}");
            output.AppendLine($"Ziel des Importdienstes: {preview.BridgeTarget}");

            if (preview.TargetDevice is not null)
            {
                output.AppendLine(
                    $"Zielsystem: TANSS-ID {preview.TargetDevice.Id} – {preview.TargetDevice.Name}");
            }
            output.AppendLine($"Vorschau-Prüfsumme: {preview.PreviewSha256}");

            if (preview.ValidatedHost is not null)
            {
                output.AppendLine(
                    $"Geprüfter VM-Host: TANSS-ID {preview.ValidatedHost.Id} – " +
                    preview.ValidatedHost.Name);
            }

            output.AppendLine();
            output.AppendLine("Feldzuordnung:");

            foreach (var mapping in preview.MappedFields)
            {
                output.AppendLine(
                    $"- {mapping.SourceField} -> {mapping.TanssField} = {mapping.Value}");
            }

            if (preview.BlockingIssues.Count > 0)
            {
                output.AppendLine();
                output.AppendLine("Übertragung blockiert:");

                foreach (var blockingIssue in preview.BlockingIssues)
                {
                    output.AppendLine("- " + blockingIssue);
                }
            }

            if (preview.Warnings.Count > 0)
            {
                output.AppendLine();
                output.AppendLine("Hinweise:");

                foreach (var warning in preview.Warnings)
                {
                    output.AppendLine("- " + warning);
                }
            }

            output.AppendLine();
            output.AppendLine("Geplante JSON-Nutzlast:");
            output.AppendLine(JsonSerializer.Serialize(preview.RequestBody, PreviewJsonOptions));

            TransferPreviewTextBox.Text = output.ToString();
            TransferPreviewTextBox.Visibility = showDetails
                ? Visibility.Visible
                : Visibility.Collapsed;
            _confirmedTransferPreview = preview;
            _confirmedTransferSelection = request;

            if (!preview.CanWrite)
            {
                ApplyBlockingFieldHighlights(preview);
                TransferPreviewExpander.IsExpanded = true;
                TransferPreviewStatusTextBlock.Foreground = ThemeManager.GetBrush("ErrorTextBrush");
                var reasonHeading = preview.BlockingIssues.Count == 1
                    ? "Blockierungsgrund:"
                    : "Blockierungsgründe:";
                TransferPreviewStatusTextBlock.Text =
                    "Übertragung blockiert. " + reasonHeading + Environment.NewLine +
                    string.Join(
                        Environment.NewLine,
                        preview.BlockingIssues.Select(issue => "• " + issue)) +
                    Environment.NewLine +
                    "Die betroffenen Eingabefelder sind rot markiert. Es wurde nichts gespeichert.";
            }
            else if (preview.Warnings.Count > 0)
            {
                TransferPreviewStatusTextBlock.Foreground = ThemeManager.GetBrush("WarningTextBrush");
                TransferPreviewStatusTextBlock.Text =
                    "Übertragungsvorschau erstellt. Bitte die Hinweise prüfen; es wurde nichts gespeichert.";
            }
            else
            {
                TransferPreviewStatusTextBlock.Foreground = ThemeManager.GetBrush("SuccessTextBrush");
                TransferPreviewStatusTextBlock.Text =
                    "Übertragungsvorschau erfolgreich geprüft; es wurde nichts gespeichert.";
            }

            return preview.CanWrite;
        }
        catch (ImportApiException exception)
        {
            ShowTransferPreviewError($"{exception.Title}: {exception.Message}");
            return false;
        }
        catch (TaskCanceledException)
        {
            ShowTransferPreviewError(
                "Zeitüberschreitung beim Erstellen der Übertragungsvorschau.");
            return false;
        }
        catch (HttpRequestException exception)
        {
            ShowTransferPreviewError(
                "Der TANSS-Importdienst ist nicht erreichbar oder die HTTPS-Anmeldung wurde abgewiesen. " +
                exception.Message);
            return false;
        }
        catch (ArgumentException exception)
        {
            ShowTransferPreviewError(exception.Message);
            return false;
        }
        catch (Exception exception)
        {
            ShowTransferPreviewError("Unerwarteter Fehler: " + exception.Message);
            return false;
        }
        finally
        {
            _isTransferPreviewInProgress = false;
            CreateTransferPreviewButton.IsHitTestVisible = true;
            Cursor = null;
        }
    }

    private async Task<TransferPreviewRequest?> ResolveExistingDeviceActionAsync(
        TransferPreviewRequest request)
    {
        if (request.SerialNumber is null)
        {
            return request;
        }

        var matches = await _apiClient.FindDevicesBySerialNumberAsync(
            _selectedCompany.Id,
            request.SerialNumber,
            CancellationToken.None);
        var otherCompanyMatches = matches
            .Where(match => !match.BelongsToSelectedCompany)
            .ToArray();
        var sameCompanyMatches = matches
            .Where(match => match.BelongsToSelectedCompany)
            .ToArray();

        // Firmenfremde oder mehrdeutige Treffer werden ausschließlich vom Server
        // in der Vorschau blockiert. Der Client wählt niemals selbst ein Ziel aus.
        if (otherCompanyMatches.Length > 0 || sameCompanyMatches.Length != 1)
        {
            return request;
        }

        var target = sameCompanyMatches[0];
        var actionWindow = new ExistingDeviceActionWindow(target, _selectedCompany)
        {
            Owner = this
        };

        if (actionWindow.ShowDialog() != true ||
            actionWindow.SelectedAction == ExistingDeviceAction.Cancel)
        {
            return null;
        }

        return actionWindow.SelectedAction switch
        {
            ExistingDeviceAction.Supplement => request with
            {
                WriteMode = "SUPPLEMENT",
                TargetDeviceId = target.Id
            },
            ExistingDeviceAction.Overwrite => request with
            {
                WriteMode = "OVERWRITE",
                TargetDeviceId = target.Id
            },
            _ => null
        };
    }

    private TransferPreviewRequest BuildTransferPreviewRequest()
    {
        var model = TransferModelTextBox.Text.Trim();

        if (model.Length is < 1 or > 255)
        {
            throw new ArgumentException(
                "Das TANSS-Pflichtfeld Modell muss zwischen 1 und 255 Zeichen lang sein.");
        }

        if (SendManufacturerCheckBox.IsChecked == true &&
            ManufacturerComboBox.SelectedItem is not ManufacturerOption)
        {
            throw new ArgumentException(
                "Bitte einen Hersteller aus der TANSS-Liste auswählen oder die Herstellerübertragung abwählen.");
        }

        if (SendOperatingSystemCheckBox.IsChecked == true &&
            OperatingSystemComboBox.SelectedItem is not OperatingSystemOption)
        {
            throw new ArgumentException(
                "Bitte ein Betriebssystem aus der TANSS-Liste auswählen oder die Betriebssystemübertragung abwählen.");
        }

        long? hostId = null;
        string? purchaseDate = null;
        string? guaranteeExpire = null;
        int? guaranteeMonth = null;

        if (SendPurchaseDateCheckBox.IsChecked == true)
        {
            purchaseDate = ValidateClientDate(
                TransferPurchaseDateTextBox.Text,
                "Kaufdatum");
        }

        if (SendGuaranteeExpireCheckBox.IsChecked == true)
        {
            guaranteeExpire = ValidateClientDate(
                TransferGuaranteeExpireTextBox.Text,
                "Garantieablaufdatum");
        }

        if (SendGuaranteeMonthCheckBox.IsChecked == true)
        {
            if (!int.TryParse(
                    TransferGuaranteeMonthTextBox.Text.Trim(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsedGuaranteeMonth) ||
                parsedGuaranteeMonth is < 0 or > 1200)
            {
                throw new ArgumentException(
                    "Die Garantiedauer muss eine nichtnegative Ganzzahl mit höchstens 1200 Monaten sein.");
            }

            guaranteeMonth = parsedGuaranteeMonth;
        }

        if (purchaseDate is not null && guaranteeExpire is not null &&
            ParseClientDate(purchaseDate) > ParseClientDate(guaranteeExpire))
        {
            throw new ArgumentException(
                "Das Garantieablaufdatum darf nicht vor dem Kaufdatum liegen.");
        }

        if (IsVirtualMachineCheckBox.IsChecked == true &&
            SendVmHostIdCheckBox.IsChecked == true)
        {
            if (!long.TryParse(
                    TransferVmHostIdTextBox.Text.Trim(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsedHostId) ||
                parsedHostId <= 0)
            {
                throw new ArgumentException(
                    "Bitte eine gültige positive TANSS-ID des VM-Hosts eingeben.");
            }

            hostId = parsedHostId;
        }

        var networkAdapters = _adapterTransferItems
            .Where(item =>
                item.IsSelected &&
                (item.SendAdapterName || item.SendMacAddress || item.SendIpv4))
            .Select(item => new TransferPreviewNetworkAdapter
            {
                Remark = item.SendAdapterName
                    ? item.TransferAdapterName
                    : null,
                Mac = item.SendMacAddress
                    ? item.TransferMacAddress
                    : null,
                Ip = item.SendIpv4
                    ? item.TransferIpv4
                    : null,
                Dhcp = item.Adapter.DhcpEnabled == true
            })
            .ToArray();

        return new TransferPreviewRequest
        {
            Name = SendHostnameCheckBox.IsChecked == true
                ? TransferHostnameTextBox.Text
                : null,
            Model = model,
            ManufacturerId = SendManufacturerCheckBox.IsChecked == true &&
                             ManufacturerComboBox.SelectedItem is ManufacturerOption manufacturer
                ? manufacturer.Id
                : null,
            OsId = SendOperatingSystemCheckBox.IsChecked == true &&
                   OperatingSystemComboBox.SelectedItem is OperatingSystemOption operatingSystem
                ? operatingSystem.Id
                : null,
            ArticleNumber = SendArticleNumberCheckBox.IsChecked == true
                ? TransferArticleNumberTextBox.Text
                : null,
            ManufacturerNumber = GetDetectedWortmannManufacturerNumber(),
            SerialNumber = SendSerialNumberCheckBox.IsChecked == true
                ? TransferSerialNumberTextBox.Text
                : null,
            TeamviewerId = SendTeamViewerIdCheckBox.IsChecked == true
                ? TransferTeamViewerIdTextBox.Text
                : null,
            Remark = SendRemarkCheckBox.IsChecked == true
                ? TransferRemarkTextBox.Text
                : null,
            InternalRemark = SendInternalRemarkCheckBox.IsChecked == true
                ? TransferInternalRemarkTextBox.Text
                : null,
            Server = IsServerCheckBox.IsChecked == true,
            PurchaseDate = purchaseDate,
            GuaranteeMonth = guaranteeMonth,
            GuaranteeExpire = guaranteeExpire,
            GuaranteeRemark = SendGuaranteeRemarkCheckBox.IsChecked == true
                ? TransferGuaranteeRemarkTextBox.Text
                : null,
            IsVirtualMachine = IsVirtualMachineCheckBox.IsChecked == true,
            HostId = hostId,
            NetworkAdapters = networkAdapters
        };
    }

    private string? GetDetectedWortmannManufacturerNumber()
    {
        if (_detectedWortmannWarranty is null ||
            SendSerialNumberCheckBox.IsChecked != true ||
            string.IsNullOrWhiteSpace(_detectedWortmannWarranty.ArticleNumber) ||
            !string.Equals(
                _detectedWortmannWarranty.SerialNumber.Trim(),
                TransferSerialNumberTextBox.Text.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return _detectedWortmannWarranty.ArticleNumber.Trim();
    }

    private static string ValidateClientDate(string value, string fieldName)
    {
        var normalized = value.Trim();

        if (normalized.Length != 6 || normalized.Any(character => !char.IsDigit(character)))
        {
            throw new ArgumentException(
                $"{fieldName} muss exakt im Format ddMMyy angegeben werden.");
        }

        try
        {
            _ = ParseClientDate(normalized);
            return normalized;
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ArgumentException(
                $"{fieldName} enthält kein gültiges Kalenderdatum.");
        }
    }

    private static DateOnly ParseClientDate(string value)
    {
        var day = int.Parse(value.AsSpan(0, 2), CultureInfo.InvariantCulture);
        var month = int.Parse(value.AsSpan(2, 2), CultureInfo.InvariantCulture);
        var year = 2000 + int.Parse(value.AsSpan(4, 2), CultureInfo.InvariantCulture);
        return new DateOnly(year, month, day);
    }

    private void InvalidateTransferPreview()
    {
        if (!IsInitialized)
        {
            return;
        }

        ClearBlockingFieldHighlights();
        TransferPreviewStatusTextBlock.Text = string.Empty;
        TransferPreviewStatusTextBlock.Visibility = Visibility.Collapsed;
        TransferPreviewTextBox.Text = string.Empty;
        TransferPreviewTextBox.Visibility = Visibility.Collapsed;
        _confirmedTransferPreview = null;
        _confirmedTransferSelection = null;
        CreateDeviceButton.IsEnabled = true;
        CreateDeviceButton.Content = "System nach TANSS übertragen";
        DeviceCreationStatusTextBlock.Text = string.Empty;
        DeviceCreationStatusTextBlock.Visibility = Visibility.Collapsed;
    }

    private void ShowTransferPreviewError(string message)
    {
        ClearBlockingFieldHighlights();
        TransferPreviewStatusTextBlock.Foreground = ThemeManager.GetBrush("ErrorTextBrush");
        TransferPreviewStatusTextBlock.Text = message;
        TransferPreviewStatusTextBlock.Visibility = Visibility.Visible;
        TransferPreviewTextBox.Text = string.Empty;
        TransferPreviewTextBox.Visibility = Visibility.Collapsed;
        _confirmedTransferPreview = null;
        _confirmedTransferSelection = null;
        CreateDeviceButton.IsEnabled = true;
    }

    private void ApplyBlockingFieldHighlights(TransferPreviewResponse preview)
    {
        ClearBlockingFieldHighlights();

        var fields = new HashSet<string>(
            preview.BlockingFields,
            StringComparer.OrdinalIgnoreCase);

        // Kompatibilität mit Servern, die noch keine strukturierten
        // Feldkennungen in der Vorschau liefern.
        foreach (var issue in preview.BlockingIssues)
        {
            if (issue.Contains("Hostname", StringComparison.OrdinalIgnoreCase))
            {
                fields.Add("hostname");
            }

            if (issue.Contains("Seriennummer", StringComparison.OrdinalIgnoreCase))
            {
                fields.Add("serialNumber");
            }
        }

        foreach (var field in fields)
        {
            if (GetControlForBlockingField(field) is Control control)
            {
                control.SetResourceReference(Control.BorderBrushProperty, "ErrorTextBrush");
                control.BorderThickness = new Thickness(2);
            }
        }
    }

    private void ClearBlockingFieldHighlights()
    {
        foreach (var control in GetHighlightableControls())
        {
            control.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
            control.BorderThickness = new Thickness(1);
        }
    }

    private Control? GetControlForBlockingField(string field) => field switch
    {
        "hostname" => TransferHostnameTextBox,
        "model" => TransferModelTextBox,
        "manufacturerId" => ManufacturerComboBox,
        "osId" => OperatingSystemComboBox,
        "articleNumber" => TransferArticleNumberTextBox,
        "serialNumber" => TransferSerialNumberTextBox,
        "teamviewerId" => TransferTeamViewerIdTextBox,
        "vmHostId" or "hostId" => TransferVmHostIdTextBox,
        "purchaseDate" => TransferPurchaseDateTextBox,
        "guaranteeMonth" => TransferGuaranteeMonthTextBox,
        "guaranteeExpire" => TransferGuaranteeExpireTextBox,
        "guaranteeRemark" => TransferGuaranteeRemarkTextBox,
        _ => null
    };

    private IEnumerable<Control> GetHighlightableControls()
    {
        yield return TransferHostnameTextBox;
        yield return TransferModelTextBox;
        yield return ManufacturerComboBox;
        yield return OperatingSystemComboBox;
        yield return TransferArticleNumberTextBox;
        yield return TransferSerialNumberTextBox;
        yield return TransferTeamViewerIdTextBox;
        yield return TransferVmHostIdTextBox;
        yield return TransferPurchaseDateTextBox;
        yield return TransferGuaranteeMonthTextBox;
        yield return TransferGuaranteeExpireTextBox;
        yield return TransferGuaranteeRemarkTextBox;
    }

    private async void CreateDeviceButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isDeviceCreationInProgress)
        {
            return;
        }

        if (_confirmedTransferPreview is null ||
            _confirmedTransferSelection is null ||
            !_confirmedTransferPreview.CanWrite)
        {
            DeviceCreationStatusTextBlock.Visibility = Visibility.Visible;
            DeviceCreationStatusTextBlock.Foreground = ThemeManager.GetBrush("PrimaryTextBrush");
            DeviceCreationStatusTextBlock.Text =
                "Eingaben werden serverseitig geprüft. " + GetAuthenticationActionHint();

            if (!await PrepareTransferPreviewAsync(showDetails: false))
            {
                ShowDeviceCreationError(TransferPreviewStatusTextBlock.Text);
                TransferPreviewExpander.IsExpanded = true;
                return;
            }
        }

        var confirmedSelection = _confirmedTransferSelection ??
            throw new InvalidOperationException("Die geprüfte Übertragungsauswahl fehlt.");
        var confirmedPreview = _confirmedTransferPreview ??
            throw new InvalidOperationException("Die geprüfte Übertragungsvorschau fehlt.");
        var hostname = confirmedSelection.Name ?? "(kein Hostname)";
        var model = confirmedSelection.Model ?? "(kein Modell)";
        var sensitiveText = confirmedSelection.InternalRemark is null
            ? string.Empty
            : Environment.NewLine +
              "Die Auswahl enthält außerdem eine interne Bemerkung bzw. ein Passwort.";
        var warningText = confirmedPreview.Warnings.Count == 0
            ? string.Empty
            : Environment.NewLine + Environment.NewLine +
              "Hinweise:" + Environment.NewLine +
              string.Join(
                  Environment.NewLine,
                  confirmedPreview.Warnings.Select(warning => "• " + warning));
        var confirmation = MessageBox.Show(
            this,
            $"Die bestätigte TANSS-Übertragung wird jetzt ausgeführt.{Environment.NewLine}{Environment.NewLine}" +
            $"Aktion: {confirmedPreview.WriteAction}{Environment.NewLine}" +
            $"Kunde: {_selectedCompany.CustomerNumber} – {_selectedCompany.Name}{Environment.NewLine}" +
            $"Hostname: {hostname}{Environment.NewLine}" +
            $"Modell: {model}" + sensitiveText + warningText +
            $"{Environment.NewLine}{Environment.NewLine}Übertragung verbindlich ausführen?",
            "TANSS-Übertragung verbindlich ausführen",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        _isDeviceCreationInProgress = true;
        CreateDeviceButton.IsHitTestVisible = false;
        CreateTransferPreviewButton.IsHitTestVisible = false;
        Cursor = System.Windows.Input.Cursors.Wait;
        DeviceCreationStatusTextBlock.Visibility = Visibility.Visible;
        DeviceCreationStatusTextBlock.Foreground = ThemeManager.GetBrush("PrimaryTextBrush");
        CreatedDeviceIdBorder.Visibility = Visibility.Collapsed;
        CreatedDeviceHyperlink.NavigateUri = null;
        CreatedDeviceLinkRun.Text = string.Empty;
        DeviceCreationStatusTextBlock.Text =
            "TANSS-Übertragung läuft. Fenster nicht schließen. " +
            GetAuthenticationActionHint();

        try
        {
            var result = await _apiClient.CreateDeviceAsync(
                _selectedCompany.Id,
                confirmedSelection,
                confirmedPreview.PreviewSha256,
                CancellationToken.None);

            var wasUpdated = string.Equals(
                result.Status,
                "updated",
                StringComparison.OrdinalIgnoreCase);
            var successAction = wasUpdated ? "aktualisiert" : "angelegt";
            var writtenDevice = result.Device ??
                throw new InvalidOperationException("Die TANSS-Gerätebestätigung fehlt.");
            var writtenCompany = result.Company ??
                throw new InvalidOperationException("Die TANSS-Kundenbestätigung fehlt.");

            DeviceCreationStatusTextBlock.Foreground = ThemeManager.GetBrush("SuccessTextBrush");
            DeviceCreationStatusTextBlock.Text =
                $"System erfolgreich in TANSS {successAction}: {writtenDevice.Name} – " +
                $"Kunde {writtenCompany.CustomerNumber} – {writtenCompany.Name}.";
            Uri? tanssDeviceUri = null;

            if (!string.IsNullOrWhiteSpace(result.DeviceUrl) &&
                Uri.TryCreate(result.DeviceUrl, UriKind.Absolute, out var parsedDeviceUri) &&
                string.Equals(parsedDeviceUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                tanssDeviceUri = parsedDeviceUri;
                CreatedDeviceHyperlink.NavigateUri = tanssDeviceUri;
                CreatedDeviceLinkRun.Text = tanssDeviceUri.AbsoluteUri;
                CreatedDeviceIdBorder.Visibility = Visibility.Visible;
            }

            var linkText = tanssDeviceUri is null
                ? string.Empty
                : $"TANSS-Link: {tanssDeviceUri.AbsoluteUri}{Environment.NewLine}";

            MessageBox.Show(
                this,
                $"Das System wurde erfolgreich in TANSS {successAction}.{Environment.NewLine}{Environment.NewLine}" +
                linkText +
                $"Hostname: {writtenDevice.Name}{Environment.NewLine}" +
                $"Kunde: {writtenCompany.CustomerNumber} – {writtenCompany.Name}",
                $"TANSS-System erfolgreich {successAction}",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (ImportApiException exception)
        {
            var outcomeWarning = (int)exception.StatusCode >= 500
                ? " Der Ausgang kann unklar sein. vor einem erneuten Versuch bitte den TANSS-Eintrag prüfen."
                : string.Empty;
            ShowDeviceCreationError(
                $"{exception.Title}: {exception.Message}{outcomeWarning}");
        }
        catch (TaskCanceledException)
        {
            ShowDeviceCreationError(
                "Zeitüberschreitung bei der Übertragung. Das Ergebnis ist unklar. vor einem erneuten Versuch bitte den TANSS-Eintrag prüfen.");
        }
        catch (HttpRequestException exception)
        {
            ShowDeviceCreationError(
                "Die Verbindung zum TANSS-Importdienst ist während der Übertragung fehlgeschlagen. " +
                "Der Ausgang ist unklar. vor einem erneuten Versuch bitte den TANSS-Eintrag prüfen. " +
                exception.Message);
        }
        catch (Exception exception)
        {
            ShowDeviceCreationError(
                "Unerwarteter Fehler während der Übertragung. Der Ausgang kann unklar sein. " +
                "vor einem erneuten Versuch bitte den TANSS-Eintrag prüfen. " +
                exception.Message);
        }
        finally
        {
            _isDeviceCreationInProgress = false;
            _confirmedTransferPreview = null;
            _confirmedTransferSelection = null;
            CreateDeviceButton.IsEnabled = true;
            CreateDeviceButton.Content = "System nach TANSS übertragen";
            CreateDeviceButton.IsHitTestVisible = true;
            CreateTransferPreviewButton.IsHitTestVisible = true;
            Cursor = null;
        }
    }

    private void CreatedDeviceHyperlink_OnRequestNavigate(
        object sender,
        RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
            e.Handled = true;
        }
        catch (Exception exception)
        {
            ShowDeviceCreationError(
                "Der TANSS-Link konnte nicht geöffnet werden: " +
                exception.Message);
        }
    }

    private void CopyCreatedDeviceLinkButton_OnClick(object sender, RoutedEventArgs e)
    {
        var tanssDeviceUrl = CreatedDeviceHyperlink.NavigateUri?.AbsoluteUri;

        if (string.IsNullOrWhiteSpace(tanssDeviceUrl))
        {
            return;
        }

        try
        {
            Clipboard.SetText(tanssDeviceUrl);
            DeviceCreationStatusTextBlock.Foreground = ThemeManager.GetBrush("SuccessTextBrush");
            DeviceCreationStatusTextBlock.Text =
                "Der TANSS-Link wurde in die Zwischenablage kopiert.";
        }
        catch (Exception exception)
        {
            ShowDeviceCreationError(
                "Der TANSS-Link konnte nicht in die Zwischenablage kopiert werden: " +
                exception.Message);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        ThemeManager.ThemeChanged -= ThemeManager_OnThemeChanged;
        _apiClient.Dispose();
        base.OnClosed(e);
    }

    private void ShowDeviceCreationError(string message)
    {
        DeviceCreationStatusTextBlock.Foreground = ThemeManager.GetBrush("ErrorTextBrush");
        DeviceCreationStatusTextBlock.Text = message;
        DeviceCreationStatusTextBlock.Visibility = Visibility.Visible;
    }

    private string GetAuthenticationActionHint() =>
        string.IsNullOrWhiteSpace(_apiClient.SecurityKeyLabel)
            ? "Die bestätigte TOTP-Sitzung wird verwendet."
            : "Die bestätigte Passkey-Sitzung wird verwendet.";

    private void UpdateCustomerDisplay()
    {
        CustomerTextBlock.Text =
            $"{_selectedCompany.CustomerNumber} – {_selectedCompany.Name} " +
            $"(TANSS-ID {_selectedCompany.Id})";
    }
}
