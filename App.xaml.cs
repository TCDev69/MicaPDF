using Microsoft.UI.Xaml;
using System;
using System.IO;

namespace MicaPDF
{
    public partial class App : Application
    {
        private Window? m_window;
        public static string? FileToOpen { get; private set; }

        public App()
        {
            InitializeStartupOptimizations();
            this.InitializeComponent();
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
                }
            }

            m_window = new MainWindow();
            m_window.Activate();
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
