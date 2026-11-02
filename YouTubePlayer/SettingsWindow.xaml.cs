using System.Windows;
using System.Windows.Controls;
using YouTubePlayer.Services;

namespace YouTubePlayer;

public partial class SettingsWindow : Window
{
    private readonly Action<SyncFrequency> _save;

    public SettingsWindow(SyncFrequency frequency, Action<SyncFrequency> save)
    {
        InitializeComponent();
        _save = save;
        foreach (var (label, value) in new[]
        {
            ("Never", SyncFrequency.Never),
            ("1 hour", SyncFrequency.OneHour),
            ("2 hours", SyncFrequency.TwoHours),
            ("6 hours", SyncFrequency.SixHours),
            ("Daily", SyncFrequency.Daily)
        })
            FrequencyBox.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        FrequencyBox.SelectedValue = frequency;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (FrequencyBox.SelectedValue is not SyncFrequency frequency)
                throw new InvalidOperationException("Select a sync frequency.");
            _save(frequency);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not save settings",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
