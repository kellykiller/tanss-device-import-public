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

public partial class MainWindow
{
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
        CustomerInputPanel.IsEnabled = false; TransferFieldsPanel.IsEnabled = false; RefreshCaptureButton.IsEnabled = false;
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
            var resolvedRequest = showDetails ? await ResolvePreviewActionAsync(request) : await ResolveExistingDeviceActionAsync(request);

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
                $"Kunde: {_selectedCompany.CustomerNumber} – {_selectedCompany.Name} " +
                $"(TANSS-ID {_selectedCompany.Id})");
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
            CustomerInputPanel.IsEnabled = true; TransferFieldsPanel.IsEnabled = true; RefreshCaptureButton.IsEnabled = true;
            CreateTransferPreviewButton.IsHitTestVisible = true;
            Cursor = null;
        }
    }

    private async Task<TransferPreviewRequest> ResolvePreviewActionAsync(TransferPreviewRequest request)
    {
        var target = await _apiClient.ResolveDeviceTargetAsync(_selectedCompany.Id, request, CancellationToken.None);
        return target is null ? request : request with { WriteMode = "SUPPLEMENT", TargetDeviceId = target.Id };
    }

    private async Task<TransferPreviewRequest?> ResolveExistingDeviceActionAsync(TransferPreviewRequest request)
    {
        var target = await _apiClient.ResolveDeviceTargetAsync(_selectedCompany.Id, request, CancellationToken.None);
        if (target is null) return request;
        var match = new DeviceSerialMatch { Id = target.Id, Name = target.Name,
            SerialNumber = request.LookupSerialNumber ?? "", Active = target.Active, Server = target.Server,
            BelongsToSelectedCompany = true, Company = new CompanyReference {
                Id = _selectedCompany.Id, CustomerNumber = _selectedCompany.CustomerNumber, Name = _selectedCompany.Name } };
        var actionWindow = new ExistingDeviceActionWindow(match, _selectedCompany) { Owner = this };
        if (actionWindow.ShowDialog() != true || actionWindow.SelectedAction == ExistingDeviceAction.Cancel) return null;
        return request with { WriteMode = actionWindow.SelectedAction == ExistingDeviceAction.Supplement ? "SUPPLEMENT" : "OVERWRITE", TargetDeviceId = target.Id };
    }

    private TransferPreviewRequest BuildTransferPreviewRequest()
    {
        if (_invoiceSelectionBusy || !IsInvoiceReadyForTransfer)
            throw new ArgumentException("Das ausgewählte PDF wurde noch nicht erfolgreich vorgemerkt. Bitte PDF erneut auswählen oder den PDF-Anhang abwählen.");
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
            purchaseDate = ClientDatePolicy.Validate(
                TransferPurchaseDateTextBox.Text,
                "Kaufdatum");
        }

        if (SendGuaranteeExpireCheckBox.IsChecked == true)
        {
            guaranteeExpire = ClientDatePolicy.Validate(
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
            ClientDatePolicy.Parse(purchaseDate) > ClientDatePolicy.Parse(guaranteeExpire))
        {
            throw new ArgumentException(
                "Das Garantieablaufdatum darf nicht vor dem Kaufdatum liegen.");
        }

        if (IsVirtualMachineCheckBox.IsChecked == true)
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

        if (_selectedCompany.Id <= 0) throw new ArgumentException("Bitte zuerst einen Kunden auswählen.");

        var networkAdapters = _adapterTransferItems
            .Where(item =>
                item.IsSelected &&
                (item.SendAdapterName || item.SendMacAddress || item.SendIpv4 || item.Dhcp || !string.IsNullOrWhiteSpace(item.TransferRemark)))
            .Select(item => new TransferPreviewNetworkAdapter
            {
                LookupMac = item.Adapter.MacAddress,
                Remark = item.CombinedRemark,
                Mac = item.SendMacAddress
                    ? item.TransferMacAddress
                    : null,
                Ip = item.SendIpv4
                    ? item.TransferIpv4
                    : null,
                Dhcp = item.Dhcp
            })
            .ToArray();

        return new TransferPreviewRequest
        {
            Name = SendHostnameCheckBox.IsChecked == true
                ? TransferHostnameTextBox.Text
                : null,
            Model = model,
            LookupSerialNumber = string.IsNullOrWhiteSpace(TransferSerialNumberTextBox.Text) ? null : TransferSerialNumberTextBox.Text.Trim(),
            LookupName = string.IsNullOrWhiteSpace(TransferHostnameTextBox.Text) ? null : TransferHostnameTextBox.Text.Trim(),
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
            ManufacturerNumber = SendManufacturerNumberCheckBox.IsChecked == true ? (string.IsNullOrWhiteSpace(TransferManufacturerNumberTextBox.Text) ? GetDetectedWortmannManufacturerNumber() : TransferManufacturerNumberTextBox.Text.Trim()) : null,
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
            NetworkAdapters = networkAdapters,
            InvoiceAttachment = AttachInvoiceCheckBox.IsChecked == true ? _invoiceSelection : new InvoiceSelection { Mode = "NONE" }
        };
    }

    private string? GetDetectedWortmannManufacturerNumber()
    {
        if (_detectedWortmannWarranty is null ||
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
        UpdateTransferAvailability();
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
        UpdateTransferAvailability();
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

        if (!await PrepareTransferPreviewAsync(showDetails: false))
        {
            ShowDeviceCreationError(TransferPreviewStatusTextBlock.Text);
            return;
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
        CustomerInputPanel.IsEnabled = false;
        TransferFieldsPanel.IsEnabled = false;
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

            ShowInvoiceQueueResult(result);

            var wasUpdated = string.Equals(
                result.Status,
                "updated",
                StringComparison.OrdinalIgnoreCase);
            var successAction = wasUpdated ? "aktualisiert" : "angelegt";
            var writtenDevice = result.Device ??
                throw new InvalidOperationException("Die TANSS-Gerätebestätigung fehlt.");
            _ = result.Company ??
                throw new InvalidOperationException("Die TANSS-Kundenbestätigung fehlt.");

            DeviceCreationStatusTextBlock.Foreground = ThemeManager.GetBrush("SuccessTextBrush");
            DeviceCreationStatusTextBlock.Text =
                $"System erfolgreich in TANSS {successAction}: {writtenDevice.Name} – " +
                $"Kunde {_selectedCompany.CustomerNumber} – {_selectedCompany.Name}.";
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
                $"Kunde: {_selectedCompany.CustomerNumber} – {_selectedCompany.Name}",
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
            CustomerInputPanel.IsEnabled = true;
            TransferFieldsPanel.IsEnabled = true;
            _confirmedTransferPreview = null;
            _confirmedTransferSelection = null;
            UpdateTransferAvailability();
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

}
