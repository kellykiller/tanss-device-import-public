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
        var revision = _contextRevision;
        var beforeLookup = TransferArticleNumberTextBox.Text.Trim();
        bool IsCurrent() => revision == _contextRevision && string.Equals(serialNumber, TransferSerialNumberTextBox.Text.Trim(), StringComparison.OrdinalIgnoreCase);
        bool CanApply() => IsCurrent() && TransferArticleNumberTextBox.Text.Trim() == beforeLookup && (beforeLookup.Length == 0 || beforeLookup == _automaticallyAppliedSapItemCode);

        if (serialNumber.Length is < 1 or > 36)
        {
            if (CanApply()) ClearSapArticleNumber();
            ShowSapArticleNumberStatus(
                "Für die SAP-Suche wird eine Seriennummer mit 1 bis 36 Zeichen benötigt.",
                ThemeManager.GetBrush("WarningTextBrush"));
            return;
        }

        _isSapLookupInProgress = true;
        UpdateTransferAvailability();
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

            if (!IsCurrent()) return;
            if (!CanApply()) {
                ShowSapArticleNumberStatus("SAP-Abfrage abgeschlossen. Die manuelle Artikelnummer bleibt erhalten.", ThemeManager.GetBrush("SecondaryTextBrush"));
                return;
            }

            if (string.Equals(lookup.Resolution, "NONE", StringComparison.Ordinal))
            {
                if (CanApply()) ClearSapArticleNumber();
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

            if (CanApply()) ClearSapArticleNumber();
            ShowSapArticleNumberStatus(
                "Mehrere SAP-Artikel wurden gefunden, aber es wurde keiner ausgewählt. " +
                "Die SAP-Artikel-Nr. bleibt leer.",
                ThemeManager.GetBrush("WarningTextBrush"));
        }
        catch (ImportApiException exception)
        {
            if (!IsCurrent()) return;
            if (CanApply()) ClearSapArticleNumber();
            ShowSapArticleNumberStatus(
                $"{exception.Title}: {exception.Message} " +
                "Die übrige Systemerfassung bleibt nutzbar.",
                ThemeManager.GetBrush("WarningTextBrush"));
        }
        catch (TaskCanceledException)
        {
            if (!IsCurrent()) return;
            if (CanApply()) ClearSapArticleNumber();
            ShowSapArticleNumberStatus(
                "Zeitüberschreitung bei der SAP-Abfrage. " +
                "Die übrige Systemerfassung bleibt nutzbar.",
                ThemeManager.GetBrush("WarningTextBrush"));
        }
        catch (HttpRequestException exception)
        {
            if (!IsCurrent()) return;
            if (CanApply()) ClearSapArticleNumber();
            ShowSapArticleNumberStatus(
                "Der Importdienst ist für die SAP-Abfrage nicht erreichbar. " +
                exception.Message,
                ThemeManager.GetBrush("WarningTextBrush"));
        }
        catch (ArgumentException exception)
        {
            if (!IsCurrent()) return;
            if (CanApply()) ClearSapArticleNumber();
            ShowSapArticleNumberStatus(
                exception.Message,
                ThemeManager.GetBrush("WarningTextBrush"));
        }
        catch (Exception exception)
        {
            if (!IsCurrent()) return;
            if (CanApply()) ClearSapArticleNumber();
            ShowSapArticleNumberStatus(
                "Unerwarteter Fehler bei der SAP-Abfrage: " + exception.Message,
                ThemeManager.GetBrush("WarningTextBrush"));
        }
        finally
        {
            if (revision != _contextRevision) _lookupTimer.Start();
            _isSapLookupInProgress = false;
            UpdateTransferAvailability();
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

}
