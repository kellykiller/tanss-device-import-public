using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TanssSystemCapture.Client;

public partial class SapItemSelectionWindow : Window
{
    public SapItemSelectionWindow(
        string serialNumber,
        IReadOnlyList<string> itemCodes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serialNumber);
        ArgumentNullException.ThrowIfNull(itemCodes);

        if (itemCodes.Count < 2)
        {
            throw new ArgumentException(
                "Für die SAP-Auswahl müssen mindestens zwei Artikel vorhanden sein.",
                nameof(itemCodes));
        }

        InitializeComponent();
        ThemeManager.Attach(this);

        InformationTextBlock.Text =
            $"Zur Seriennummer {serialNumber} wurden mehrere Artikelnummern gefunden. " +
            "Bitte den tatsächlich passenden Artikel auswählen. Es wird keine automatische Vorauswahl getroffen.";
        ItemCodesListBox.ItemsSource = itemCodes;
    }

    public string? SelectedItemCode { get; private set; }

    private void ItemCodesListBox_OnSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        ApplyButton.IsEnabled =
            ItemCodesListBox.SelectedItem is string itemCode &&
            !string.IsNullOrWhiteSpace(itemCode);
    }

    private void ItemCodesListBox_OnMouseDoubleClick(
        object sender,
        MouseButtonEventArgs e)
    {
        if (ApplyButton.IsEnabled)
        {
            ApplySelection();
        }
    }

    private void ApplyButton_OnClick(object sender, RoutedEventArgs e)
    {
        ApplySelection();
    }

    private void ApplySelection()
    {
        if (ItemCodesListBox.SelectedItem is not string itemCode ||
            string.IsNullOrWhiteSpace(itemCode))
        {
            return;
        }

        SelectedItemCode = itemCode.Trim();
        DialogResult = true;
    }
}
