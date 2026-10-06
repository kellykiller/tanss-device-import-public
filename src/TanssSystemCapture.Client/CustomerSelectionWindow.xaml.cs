using System.Windows;
using TanssSystemCapture.Client.Services;
namespace TanssSystemCapture.Client;
public partial class CustomerSelectionWindow : Window
{
    private bool _busy;
    public ClientSettings? SelectedSettings { get; private set; }
    public ImportApiClient? SelectedApiClient { get; private set; }
    public CustomerSelectionWindow() { InitializeComponent(); ThemeManager.Attach(this); Loaded += (_, _) => ServerAddressTextBox.Focus(); }
    private void Input_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        ContinueButton.IsEnabled = !_busy && Uri.TryCreate(ServerAddressTextBox.Text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && TotpCodePasswordBox.Password.Length == 6 && TotpCodePasswordBox.Password.All(c => c is >= '0' and <= '9');
    }
    private async void ContinueButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true; ContinueButton.IsEnabled = false;
        ServerAddressTextBox.IsEnabled = false; TotpCodePasswordBox.IsEnabled = false;
        StatusTextBlock.Text = "Anmeldung wird geprüft …";
        ImportApiClient? client = null;
        try {
            var settings = ClientSettings.Create(ServerAddressTextBox.Text);
            client = new ImportApiClient(settings.ImportApiBaseUri, TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));
            await client.AuthenticateWithTotpAsync(TotpCodePasswordBox.Password.Trim(), CancellationToken.None);
            SelectedSettings = settings; SelectedApiClient = client;
            TotpCodePasswordBox.Clear(); DialogResult = true;
        } catch (Exception exception) {
            client?.Dispose(); StatusTextBlock.Foreground = ThemeManager.GetBrush("ErrorTextBrush");
            StatusTextBlock.Text = exception.Message; TotpCodePasswordBox.Clear();
        } finally {
            _busy = false; ServerAddressTextBox.IsEnabled = true; TotpCodePasswordBox.IsEnabled = true;
            Input_OnChanged(this, new RoutedEventArgs());
        }
    }
}
