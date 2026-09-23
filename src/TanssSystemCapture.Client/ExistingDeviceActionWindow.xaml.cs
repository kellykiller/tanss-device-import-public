using System.Windows;
using TanssSystemCapture.Client.Models;

namespace TanssSystemCapture.Client;

public partial class ExistingDeviceActionWindow : Window
{
    public ExistingDeviceActionWindow(
        DeviceSerialMatch match,
        Company selectedCompany)
    {
        InitializeComponent();
        ThemeManager.Attach(this);

        DeviceTextBlock.Text =
            $"TANSS-ID {match.Id} – {match.Name}{Environment.NewLine}" +
            $"Kunde {selectedCompany.CustomerNumber} – {selectedCompany.Name}{Environment.NewLine}" +
            $"Seriennummer {match.SerialNumber}";
    }

    public ExistingDeviceAction SelectedAction { get; private set; } =
        ExistingDeviceAction.Cancel;

    private void SupplementButton_OnClick(object sender, RoutedEventArgs e)
    {
        SelectedAction = ExistingDeviceAction.Supplement;
        DialogResult = true;
    }

    private void OverwriteButton_OnClick(object sender, RoutedEventArgs e)
    {
        SelectedAction = ExistingDeviceAction.Overwrite;
        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        SelectedAction = ExistingDeviceAction.Cancel;
        DialogResult = false;
    }
}

public enum ExistingDeviceAction
{
    Cancel,
    Supplement,
    Overwrite
}

