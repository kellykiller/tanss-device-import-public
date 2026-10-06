using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TanssSystemCapture.Client;
using TanssSystemCapture.Client.Models;
using TanssSystemCapture.Client.Services;

internal static class Program
{
    internal static void Run()
    {
        var app = new App { SuppressInteractiveStartupForTests = true }; app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var theme = typeof(App).Assembly.GetType("TanssSystemCapture.Client.ThemeManager")!;
        theme.GetMethod("ApplyTheme")!.Invoke(null, new object[] { true });
        var login = new CustomerSelectionWindow();
        Require(((TextBox)login.FindName("ServerAddressTextBox")).Text == "", "Serveradresse beim Start nicht leer");
        Require(((PasswordBox)login.FindName("TotpCodePasswordBox")).Password == "", "TOTP beim Start nicht leer");
        Require(!((Button)login.FindName("ContinueButton")).IsEnabled, "Anmeldung ohne Eingaben möglich");
        Require(login.FindName("CustomerNumberTextBox") is null, "Kundenauswahl im Startfenster");
        Render(login, "login.png", 480, 450);
        var settings = ClientSettings.Create("https://127.0.0.1:1");
        using var api = new ImportApiClient(settings.ImportApiBaseUri, TimeSpan.FromSeconds(1));
        var main = new MainWindow(settings, api, new SystemCaptureService(), new Company { Id = 42, CustomerNumber = "10001", Name = "Beispielfirma GmbH" });
        foreach (var name in new[] { "TransferRemarkTextBox", "TransferInternalRemarkTextBox", "TransferVmHostIdTextBox", "InvoiceStatusTextBlock", "CustomerNumberTextBox" }) Require(main.FindName(name) is not null, "Feld fehlt: " + name);
        Require(main.FindName("ThemeToggleButton") is null, "Umschaltbarer Dark Mode");
        Require(main.FindName("SendVmHostIdCheckBox") is null, "Host-ID bleibt optional");
        ((TextBox)main.FindName("TransferHostnameTextBox")).Text = "DEMO-CLIENT-01";
        ((TextBox)main.FindName("TransferModelTextBox")).Text = "TERRA MOBILE GAMER ELITE 3 Ultra 7-155H W11P";
        ((CheckBox)main.FindName("IsVirtualMachineCheckBox")).IsChecked = true;
        var builder = typeof(MainWindow).GetMethod("BuildTransferPreviewRequest", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try { builder.Invoke(main, null); throw new Exception("VM ohne Host zugelassen"); }
        catch (TargetInvocationException error) when (error.InnerException is ArgumentException) { }
        ((TextBox)main.FindName("TransferVmHostIdTextBox")).Text = "900";
        var selection = (TransferPreviewRequest)builder.Invoke(main, null)!;
        Require(selection.HostId == 900 && selection.IsVirtualMachine, "VM-Zuordnung fehlt");
        Render(main, "main.png", 1380, 820);
        RunOnDispatcher(InvoicePreviewTests.RunAsync());
        RunOnDispatcher(TransferSelectionTests.RunAsync());
        main.Close(); login.Close(); app.Shutdown();
        Console.WriteLine("UI smoke: leerer Login, Felder, Dark Mode, VM-Hostpflicht und Layout gerendert: OK");
    }
    private static void RunOnDispatcher(Task task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Render(Window window, string filename, int width, int height)
    {
        var view = (FrameworkElement)window.Content;
        view.Measure(new Size(width - 32, height - 48)); view.Arrange(new Rect(0, 0, width - 32, height - 48)); view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        bitmap.Render(background); bitmap.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory("artifacts/ui"); using (var output = File.Create(Path.Combine("artifacts/ui", filename))) encoder.Save(output);
        Console.WriteLine("Rendered UI: " + filename);
    }
}
