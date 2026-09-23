using System.Net.Http;
using System.Windows;
using TanssSystemCapture.Client.Services;

namespace TanssSystemCapture.Client;

public partial class SecurityKeyRegistrationWindow : Window
{
    private readonly ClientSettings _settings;

    public SecurityKeyRegistrationWindow(ClientSettings settings)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        _settings = settings;
        KeyLabelTextBox.Text = $"Passkey {DateTime.Now:yyyy-MM-dd}";
        Loaded += (_, _) => KeyLabelTextBox.Focus();
    }

    public string RegisteredKeyLabel { get; private set; } = string.Empty;

    private async void RegisterButton_OnClick(
        object sender,
        RoutedEventArgs e)
    {
        var label = KeyLabelTextBox.Text.Trim();
        var enrollmentCode = EnrollmentCodePasswordBox.Password.Trim();

        if (label.Length is < 1 or > 80)
        {
            ShowError("Bitte eine Bezeichnung mit 1 bis 80 Zeichen eingeben.");
            return;
        }

        if (enrollmentCode.Length is < 10 or > 100)
        {
            ShowError("Bitte den vollständigen einmaligen Registrierungscode eingeben.");
            return;
        }

        SetBusy(true);
        StatusTextBlock.Foreground = ThemeManager.GetBrush("PrimaryTextBrush");
        StatusTextBlock.Text =
            "Windows öffnet jetzt die Passkey-Abfrage. Bitte den gewünschten Hardware-Schlüssel einstecken und berühren.";

        using var apiClient = new ImportApiClient(
            _settings.ImportApiBaseUri,
            requestTimeout: TimeSpan.FromSeconds(
                _settings.RequestTimeoutSeconds));

        try
        {
            var result = await apiClient.RegisterSecurityKeyAsync(
                enrollmentCode,
                label,
                CancellationToken.None);
            RegisteredKeyLabel = result.KeyLabel;
            EnrollmentCodePasswordBox.Clear();
            DialogResult = true;
        }
        catch (ImportApiException exception)
        {
            ShowError($"{exception.Title}: {exception.Message}");
        }
        catch (TaskCanceledException)
        {
            ShowError(
                "Die Registrierung wurde abgebrochen oder nicht rechtzeitig durch Berührung bestätigt.");
        }
        catch (HttpRequestException exception)
        {
            ShowError(
                "Der TANSS-Importdienst ist nicht erreichbar. " +
                exception.Message);
        }
        catch (ArgumentException exception)
        {
            ShowError(exception.Message);
        }
        catch (Exception exception)
        {
            ShowError(
                "Die Windows-Passkey-Abfrage ist fehlgeschlagen: " +
                exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool isBusy)
    {
        KeyLabelTextBox.IsEnabled = !isBusy;
        EnrollmentCodePasswordBox.IsEnabled = !isBusy;
        RegisterButton.IsEnabled = !isBusy;
        Cursor = isBusy ? System.Windows.Input.Cursors.Wait : null;
    }

    private void ShowError(string message)
    {
        StatusTextBlock.Foreground = ThemeManager.GetBrush("ErrorTextBrush");
        StatusTextBlock.Text = message;
    }
}
