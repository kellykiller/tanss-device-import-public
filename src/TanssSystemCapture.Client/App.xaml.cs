using System.Windows;
using TanssSystemCapture.Client.Services;

namespace TanssSystemCapture.Client;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemeManager.ApplyTheme(darkMode: true);

        try
        {
            var systemCaptureService = new SystemCaptureService();

            var customerSelectionWindow = new CustomerSelectionWindow();

            if (customerSelectionWindow.ShowDialog() != true ||
                customerSelectionWindow.SelectedSettings is null ||
                customerSelectionWindow.SelectedCompany is null ||
                customerSelectionWindow.SelectedApiClient is null)
            {
                Shutdown();
                return;
            }

            var mainWindow = new MainWindow(
                customerSelectionWindow.SelectedSettings,
                customerSelectionWindow.SelectedApiClient,
                systemCaptureService,
                customerSelectionWindow.SelectedCompany);

            MainWindow = mainWindow;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            mainWindow.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "TANSS Systemerfassung – Startfehler",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(1);
        }
    }

}
