using Microsoft.UI.Xaml;
using System;
using System.Linq;

namespace MicaPDF
{
    public partial class App : Application
    {
        private Window? m_window;
        public static string? FileToOpen { get; private set; }

        public App()
        {
            this.InitializeComponent();
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            // Check for command line arguments
            var commandLineArgs = Environment.GetCommandLineArgs();
            if (commandLineArgs.Length > 1)
            {
                // The first argument is the executable path, second is the file to open
                var filePath = commandLineArgs[1];
                if (System.IO.File.Exists(filePath) && filePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    FileToOpen = filePath;
                }
            }

            m_window = new MainWindow();
            m_window.Activate();
        }
    }
}
