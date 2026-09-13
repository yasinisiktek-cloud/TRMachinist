using System.Windows;
using System.Windows.Media;

namespace TRMachinist.Simulator;

// Presentation only. No simulation clock, stock request or camera state lives here.
public partial class MainWindow
{
    public static readonly DependencyProperty SceneVisibilityProperty = DependencyProperty.Register(
        nameof(SceneVisibility), typeof(Visibility), typeof(MainWindow), new PropertyMetadata(Visibility.Hidden));
    public static readonly DependencyProperty WelcomeVisibilityProperty = DependencyProperty.Register(
        nameof(WelcomeVisibility), typeof(Visibility), typeof(MainWindow), new PropertyMetadata(Visibility.Visible));

    public Visibility SceneVisibility
    {
        get => (Visibility)GetValue(SceneVisibilityProperty);
        private set => SetValue(SceneVisibilityProperty, value);
    }
    public Visibility WelcomeVisibility
    {
        get => (Visibility)GetValue(WelcomeVisibilityProperty);
        private set => SetValue(WelcomeVisibilityProperty, value);
    }

    private double _setupWidth = 266;
    private double _inspectorWidth = 360;
    private bool _restoreSetup = true;
    private bool _restoreInspector = true;

    private void RefreshWorkspacePresentation()
    {
        var loaded = _machine is not null || _job is not null;
        SceneVisibility = loaded ? Visibility.Visible : Visibility.Hidden;
        WelcomeVisibility = loaded ? Visibility.Collapsed : Visibility.Visible;
        if (IpwStateText is null) return;
        var ipwActive = _scene?.IsAlpha4Test2Active == true;
        IpwStateText.Text = ipwActive ? "Canlı stok etkin" : "IPW kapalı";
        IpwStateText.Foreground = (Brush)FindResource(ipwActive ? "Primary" : "ForegroundMuted");
    }

    private void ToggleSetup_Click(object sender, RoutedEventArgs e) =>
        SetSetupVisible(SetupPane.Visibility != Visibility.Visible);

    private void ToggleInspector_Click(object sender, RoutedEventArgs e) =>
        SetInspectorVisible(InspectorPane.Visibility != Visibility.Visible);

    private void FocusWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (SetupPane.Visibility == Visibility.Visible || InspectorPane.Visibility == Visibility.Visible)
        {
            _restoreSetup = SetupPane.Visibility == Visibility.Visible;
            _restoreInspector = InspectorPane.Visibility == Visibility.Visible;
            SetSetupVisible(false);
            SetInspectorVisible(false);
        }
        else
        {
            SetSetupVisible(_restoreSetup);
            SetInspectorVisible(_restoreInspector);
        }
    }

    private void SetSetupVisible(bool visible)
    {
        if (visible == (SetupPane.Visibility == Visibility.Visible)) return;
        if (!visible) _setupWidth = SetupColumn.ActualWidth;
        SetupColumn.MinWidth = visible ? 244 : 0;
        SetupColumn.Width = new GridLength(visible ? Math.Clamp(_setupWidth, 244, Math.Max(244, ActualWidth * .30)) : 0);
        LeftSplitterColumn.Width = new GridLength(visible ? 5 : 0);
        SetupPane.Visibility = LeftSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ShowSetupButton.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetInspectorVisible(bool visible)
    {
        if (visible == (InspectorPane.Visibility == Visibility.Visible)) return;
        if (!visible) _inspectorWidth = InspectorColumn.ActualWidth;
        InspectorColumn.MinWidth = visible ? 330 : 0;
        InspectorColumn.Width = new GridLength(visible ? Math.Clamp(_inspectorWidth, 330, Math.Max(330, ActualWidth * .40)) : 0);
        RightSplitterColumn.Width = new GridLength(visible ? 5 : 0);
        InspectorPane.Visibility = RightSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ShowInspectorButton.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
    }
}
