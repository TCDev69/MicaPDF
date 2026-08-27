using Microsoft.UI.Xaml;
using System;
using System.IO;
using System.Threading.Tasks;

namespace MicaPDF
{
    public partial class App : Application
    {
        private Window? m_window;
        public static string? FileToOpen { get; private set; }

        public App()
        {
            InitializeStartupOptimizations();
            AppLog.Initialize();
            this.InitializeComponent();
            this.UnhandledException += App_UnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            var commandLineArgs = Environment.GetCommandLineArgs();
            if (commandLineArgs.Length > 1)
            {
                var filePath = commandLineArgs[1];
                if (System.IO.File.Exists(filePath) && filePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    FileToOpen = filePath;
                    AppLog.Info($"Launch with file: {Path.GetFileName(filePath)}");
                }
            }

            m_window = new MainWindow();
            m_window.Activate();
            AppLog.Info("Main window activated");
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            AppLog.Error("Unhandled UI exception", e.Exception);
            e.Handled = true;
        }

        private static void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
                AppLog.Error("Unhandled domain exception", ex);
            else
                AppLog.Error($"Unhandled domain exception: {e.ExceptionObject}");
            AppLog.Shutdown();
        }

        private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            AppLog.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        }

        private void InitializeStartupOptimizations()
        {
            try
            {
                AppContext.SetSwitch("System.Runtime.TieredCompilation", false);
                var cachePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MicaPDF");
                Directory.CreateDirectory(cachePath);
                System.Runtime.ProfileOptimization.SetProfileRoot(cachePath);
                System.Runtime.ProfileOptimization.StartProfile("startup.profile");
            }
            catch
            {
                // Best-effort optimization; ignore failures on unsupported systems
            }
        }
    }
}
