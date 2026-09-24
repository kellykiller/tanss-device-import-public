using System.Net.Http;
using System.Windows;
using TanssSystemCapture.Client.Models;
using TanssSystemCapture.Client.Services;

namespace TanssSystemCapture.Client;

public partial class CustomerSelectionWindow : Window
{
    private ClientSettings? _settings;
    private ImportApiClient? _apiClient;
    private bool _clientTransferred;
    private bool _synchronizingThemeToggle;

    public CustomerSelectionWindow(ClientSettings? initialSettings = null)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        ThemeManager.ThemeChanged += ThemeManager_OnThemeChanged;
        SynchronizeThemeToggle(ThemeManager.IsDarkMode);

        _settings = initialSettings;
        ServerAddressTextBox.Text = initialSettings?.ImportApiBaseUri.AbsoluteUri ?? string.Empty;

        Loaded += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(ServerAddressTextBox.Text))
            {
                ServerAddressTextBox.Focus();
            }
            else
            {
                CustomerNumberTextBox.Focus();
            }
        };
    }

    public Company? SelectedCompany { get; private set; }

    public ImportApiClient? SelectedApiClient { get; private set; }

    public ClientSettings? SelectedSettings { get; private set; }

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

    private async void CheckCustomerButton_OnClick(object sender, RoutedEventArgs e)
    {
        var customerNumber = CustomerNumberTextBox.Text.Trim();

        if (customerNumber.Length is < 1 or > 50)
        {
            ShowError("Die Kundennummer muss zwischen 1 und 50 Zeichen lang sein.");
            return;
        }

        SetBusy(true);
        InvalidateSelection();
        StatusTextBlock.Foreground = ThemeManager.GetBrush("PrimaryTextBrush");
        StatusTextBlock.Text =
            "TOTP-Code wird geprüft und anschließend der Kunde abgerufen.";

        try
        {
            var apiClient = await EnsureAuthenticatedApiClientAsync();
            var company = await apiClient.ResolveCompanyAsync(
                customerNumber,
                CancellationToken.None);

            SelectedCompany = company;
            ShowCompany(company);
            StatusTextBlock.Foreground = ThemeManager.GetBrush("SuccessTextBrush");
            var sessionInformation =
                apiClient.AuthenticationSessionExpiresUtc is DateTimeOffset expiresUtc
                    ? $" TOTP-Sitzung gültig bis {expiresUtc.ToLocalTime():G}."
                    : string.Empty;
            StatusTextBlock.Text =
                "Kunde wurde eindeutig gefunden. Bitte Angaben prüfen und bestätigen." +
                sessionInformation;
            ContinueButton.IsEnabled = true;
        }
        catch (ImportApiException exception)
        {
            if (exception.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                ResetApiClient();
            }

            ShowError($"{exception.Title}: {exception.Message}");
        }
        catch (TaskCanceledException)
        {
            ShowError(
                "Zeitüberschreitung beim Zugriff auf den TANSS-Importdienst. " +
                "Die TOTP-Anmeldung oder die HTTPS-Anfrage wurde nicht rechtzeitig abgeschlossen. " +
                "Bitte erneut versuchen.");
        }
        catch (HttpRequestException exception)
        {
            ShowError(
                "Der TANSS-Importdienst ist nicht erreichbar oder die HTTPS-Verbindung ist fehlgeschlagen. " +
                exception.Message);
        }
        catch (ArgumentException exception)
        {
            ShowError(exception.Message);
        }
        catch (ClientConfigurationException exception)
        {
            ShowError(exception.Message);
        }
        catch (Exception exception)
        {
            ShowError("Unerwarteter Fehler: " + exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<ImportApiClient> EnsureAuthenticatedApiClientAsync()
    {
        var settings = ClientSettings.Create(ServerAddressTextBox.Text);
        if (_apiClient is not null &&
            _settings?.ImportApiBaseUri == settings.ImportApiBaseUri)
        {
            return _apiClient;
        }

        ResetApiClient();
        _settings = settings;

        var totpCode = TotpCodePasswordBox.Password.Trim();

        if (totpCode.Length != 6 ||
            totpCode.Any(character => character is < '0' or > '9'))
        {
            throw new ArgumentException(
                "Der TOTP-Code muss genau sechs Ziffern enthalten.");
        }

        var totpClient = new ImportApiClient(
            settings.ImportApiBaseUri,
            requestTimeout: TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));

        try
        {
            await totpClient.AuthenticateWithTotpAsync(
                totpCode,
                CancellationToken.None);
        }
        catch
        {
            totpClient.Dispose();
            throw;
        }

        TotpCodePasswordBox.Clear();
        _apiClient = totpClient;
        return _apiClient;
    }

    private void CustomerNumberTextBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        InvalidateSelection();
        StatusTextBlock.Text = string.Empty;
    }

    private void ServerAddressTextBox_OnTextChanged(
        object sender,
        System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        ResetApiClient();
        _settings = null;
        InvalidateSelection();
        StatusTextBlock.Text = string.Empty;
    }

    private void ContinueButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedCompany is null || _apiClient is null || _settings is null)
        {
            ShowError("Es wurde noch kein Kunde erfolgreich geprüft.");
            return;
        }

        SelectedApiClient = _apiClient;
        SelectedSettings = _settings;
        _apiClient = null;
        _clientTransferred = true;
        DialogResult = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        ThemeManager.ThemeChanged -= ThemeManager_OnThemeChanged;

        if (!_clientTransferred)
        {
            ResetApiClient();
        }

        base.OnClosed(e);
    }

    private void ShowCompany(Company company)
    {
        ResultCustomerNumberTextBlock.Text = company.CustomerNumber;
        ResultCompanyIdTextBlock.Text = company.Id.ToString();

        var addressLines = new List<string> { company.Name };

        if (!string.IsNullOrWhiteSpace(company.Street))
        {
            addressLines.Add(company.Street);
        }

        var postalCodeAndCity = string.Join(
            " ",
            new[] { company.PostalCode, company.City }
                .Where(value => !string.IsNullOrWhiteSpace(value)));

        if (!string.IsNullOrWhiteSpace(postalCodeAndCity))
        {
            addressLines.Add(postalCodeAndCity);
        }

        ResultAddressTextBlock.Text = string.Join(Environment.NewLine, addressLines);
        CustomerResultBorder.Visibility = Visibility.Visible;
    }

    private void InvalidateSelection()
    {
        SelectedCompany = null;
        ContinueButton.IsEnabled = false;
        CustomerResultBorder.Visibility = Visibility.Collapsed;
    }

    private void SetBusy(bool isBusy)
    {
        CustomerNumberTextBox.IsEnabled = !isBusy;
        ServerAddressTextBox.IsEnabled = !isBusy;
        CheckCustomerButton.IsEnabled = !isBusy;
        TotpCodePasswordBox.IsEnabled = !isBusy;
        Cursor = isBusy ? System.Windows.Input.Cursors.Wait : null;
    }

    private void ShowError(string message)
    {
        InvalidateSelection();
        StatusTextBlock.Foreground = ThemeManager.GetBrush("ErrorTextBrush");
        StatusTextBlock.Text = message;
    }

    private void ResetApiClient()
    {
        _apiClient?.Dispose();
        _apiClient = null;
    }
}
