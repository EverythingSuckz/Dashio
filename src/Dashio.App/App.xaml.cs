using Microsoft.UI.Xaml;

namespace Dashio.App;

public partial class App : Application
{
    /// <summary>
    /// Set to "1" to open the window without taking focus. The UI tests use it so a test run
    /// does not grab the keyboard from whoever is using the PC.
    /// </summary>
    public const string BackgroundVariable = "DASHIO_NO_ACTIVATE";

    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        if (Environment.GetEnvironmentVariable(BackgroundVariable) == "1")
            _window.AppWindow.Show(activateWindow: false);
        else
            _window.Activate();
    }
}
