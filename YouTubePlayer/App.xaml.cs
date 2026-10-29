using System.Configuration;
using System.Data;
using System.Windows;

namespace YouTubePlayer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or TimeoutException)
        {
            MessageBox.Show($"Saved application data could not be loaded. No data has been reset.\n\n{ex.Message}",
                "YouTube Player startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
