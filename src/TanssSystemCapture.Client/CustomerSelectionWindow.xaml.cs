using System.Net.Http;
using System.Windows;
using TanssSystemCapture.Client.Models;
using TanssSystemCapture.Client.Services;

namespace TanssSystemCapture.Client;

public partial class CustomerSelectionWindow : Window
{
    private ClientSettings? _settings;
    private ImportApiClient? _apiClient;
    private ImportAuthenticationMode? _apiClientMode;
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
        StatusTextBlock.Text = GetSelectedAuthenticationMode() switch
        {
            ImportAuthenticationMode.Passkey =>
                "Passkey einstecken und nach der Windows-Aufforderung berühren. Anschließend wird der Kunde abgerufen.",
            _ => "TOTP-Code wird geprüft und anschließend der Kunde abgerufen."
        };

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
                    ? _apiClientMode == ImportAuthenticationMode.Passkey
                        ? $" Passkey-Sitzung ({apiClient.SecurityKeyLabel}) gültig bis {expiresUtc.ToLocalTime():G}."
                        : $" TOTP-Sitzung gültig bis {expiresUtc.ToLocalTime():G}."
                    : string.Empty;
            StatusTextBlock.Text =
                "Kunde wurde eindeutig gefunden. Bitte Angaben prüfen und bestätigen." +
                sessionInformation;
            ContinueButton.IsEnabled = true;
        }
        catch (ImportApiException exception)
        {
            if ((_apiClientMode is ImportAuthenticationMode.Totp or
                 ImportAuthenticationMode.Passkey) &&
                exception.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                ResetApiClient();
            }

            ShowError($"{exception.Title}: {exception.Message}");
        }
        catch (TaskCanceledException)
        {
            var timeoutDetail = GetSelectedAuthenticationMode() switch
            {
                ImportAuthenticationMode.Passkey =>
                    "Die Berührung des Passkeys wurde nicht rechtzeitig bestätigt.",
                _ =>
                    "Die TOTP-Anmeldung oder die HTTPS-Anfrage wurde nicht rechtzeitig abgeschlossen."
            };
            ShowError(
                "Zeitüberschreitung beim Zugriff auf den TANSS-Importdienst. " +
                timeoutDetail + " Bitte erneut versuchen.");
        }
        catch (HttpRequestException exception)
        {
            var authenticationDetail = GetSelectedAuthenticationMode() switch
            {
                ImportAuthenticationMode.Passkey =>
                    "die Passkey-Anmeldung wurde abgewiesen",
                _ => "die HTTPS-Verbindung ist fehlgeschlagen"
            };
            ShowError(
                $"Der TANSS-Importdienst ist nicht erreichbar oder {authenticationDetail}. " +
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
        var selectedMode = GetSelectedAuthenticationMode();

        if (_apiClient is not null &&
            _apiClientMode == selectedMode &&
            _settings?.ImportApiBaseUri == settings.ImportApiBaseUri)
        {
            return _apiClient;
        }

        ResetApiClient();
        _settings = settings;

        if (selectedMode == ImportAuthenticationMode.Passkey)
        {
            var securityKeyClient = new ImportApiClient(
                settings.ImportApiBaseUri,
                requestTimeout: TimeSpan.FromSeconds(
                    settings.RequestTimeoutSeconds));

            try
            {
                await securityKeyClient.AuthenticateWithSecurityKeyAsync(
                    CancellationToken.None);
            }
            catch
            {
                securityKeyClient.Dispose();
                throw;
            }

            _apiClient = securityKeyClient;
            _apiClientMode = selectedMode;
            return _apiClient;
        }

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
        _apiClientMode = selectedMode;
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

    private void AuthenticationMode_OnChecked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        ResetApiClient();
        InvalidateSelection();
        StatusTextBlock.Text = string.Empty;
        TotpCodePasswordBox.IsEnabled = TotpRadioButton.IsChecked == true;

        if (TotpRadioButton.IsChecked == true)
        {
            TotpCodePasswordBox.Focus();
        }
    }

    private void RegisterPasskeyButton_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        ClientSettings settings;

        try
        {
            settings = ClientSettings.Create(ServerAddressTextBox.Text);
        }
        catch (ClientConfigurationException exception)
        {
            ShowError(exception.Message);
            ServerAddressTextBox.Focus();
            return;
        }

        var registrationWindow = new SecurityKeyRegistrationWindow(settings)
        {
            Owner = this
        };

        if (registrationWindow.ShowDialog() == true)
        {
            PasskeyRadioButton.IsChecked = true;
            ResetApiClient();
            InvalidateSelection();
            StatusTextBlock.Foreground = ThemeManager.GetBrush("SuccessTextBrush");
            StatusTextBlock.Text =
                $"Passkey „{registrationWindow.RegisteredKeyLabel}“ wurde registriert und kann jetzt verwendet werden.";
        }
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
        PasskeyRadioButton.IsEnabled = !isBusy;
        TotpRadioButton.IsEnabled = !isBusy;
        RegisterPasskeyButton.IsEnabled = !isBusy;
        TotpCodePasswordBox.IsEnabled = !isBusy && TotpRadioButton.IsChecked == true;
        Cursor = isBusy ? System.Windows.Input.Cursors.Wait : null;
    }

    private void ShowError(string message)
    {
        InvalidateSelection();
        StatusTextBlock.Foreground = ThemeManager.GetBrush("ErrorTextBrush");
        StatusTextBlock.Text = message;
    }

    private ImportAuthenticationMode GetSelectedAuthenticationMode() =>
        TotpRadioButton.IsChecked == true
            ? ImportAuthenticationMode.Totp
            : ImportAuthenticationMode.Passkey;

    private void ResetApiClient()
    {
        _apiClient?.Dispose();
        _apiClient = null;
        _apiClientMode = null;
    }

    private enum ImportAuthenticationMode
    {
        Passkey,
        Totp
    }
}
