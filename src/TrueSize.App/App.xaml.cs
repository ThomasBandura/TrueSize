namespace TrueSize.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Optional start path from the command line, e.g. "TrueSize.App.exe D:\Data".
        string[] commandLine = Environment.GetCommandLineArgs();
        _window = new MainWindow(commandLine.Length > 1 ? commandLine[1] : null);
        _window.Activate();
    }
}
