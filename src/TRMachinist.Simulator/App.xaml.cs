using System.Configuration;
using System.Data;
using System.Windows;

namespace TRMachinist.Simulator;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Set the application palette only; Windows personalization is untouched.
        Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(
            System.Windows.Media.Color.FromRgb(138, 180, 248),
            Wpf.Ui.Appearance.ApplicationTheme.Dark, false, false);
        base.OnStartup(e);
    }
}

