using System;
using System.IO;
using Microsoft.UI.Xaml;

namespace Hammer5Git.Main;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        this.UnhandledException += (_, e) =>
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "crash_error.log"),
                    $"[WinUI Unhandled] {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n{e.Message}\r\n{e.Exception}\r\n\r\n");
            }
            catch { }
        };
        this.InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new Hammer5Git.App.MainWindow();
        _window.Activate();
    }
}
