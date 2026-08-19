using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CustomImageViewer.Services;

namespace CustomImageViewer;

public partial class App : Application
{
    public static string? StartupPath { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        EventManager.RegisterClassHandler(
            typeof(Window),
            Keyboard.PreviewKeyDownEvent,
            new KeyEventHandler(ClosePopupWindowOnEscape));

        AppLogService.Initialize();
        if (e.Args.FirstOrDefault() is { Length: > 0 } argument)
        {
            try
            {
                StartupPath = Path.GetFullPath(argument.Trim('"'));
            }
            catch (Exception ex)
            {
                AppLogService.Warning("Startup", "시작 경로를 해석하지 못했습니다.", ex);
            }
        }
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                AppLogService.Error("AppDomain", "처리되지 않은 치명적 오류", exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLogService.Error("BackgroundTask", "관찰되지 않은 백그라운드 작업 오류", args.Exception);
            args.SetObserved();
        };
        base.OnStartup(e);
    }

    private static void ClosePopupWindowOnEscape(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not Window window || window is MainWindow)
            return;

        e.Handled = true;
        window.Close();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLogService.Info("Application", $"프로그램을 종료했습니다. 종료 코드={e.ApplicationExitCode}");
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogService.Error("UI", "처리되지 않은 UI 오류", e.Exception);
        if (e.Exception is OutOfMemoryException or StackOverflowException or AccessViolationException) return;

        MessageBox.Show(
            "예상하지 못한 오류가 발생했습니다. 오류 기록을 저장했으며 가능한 작업은 계속할 수 있습니다.\n\n" +
            e.Exception.Message,
            "TagSeeker", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
