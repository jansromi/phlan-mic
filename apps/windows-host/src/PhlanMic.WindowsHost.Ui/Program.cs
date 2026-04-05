using System.Runtime.InteropServices;
using System.Windows.Forms;
using PhlanMic.WindowsHost;

namespace PhlanMic.WindowsHost.Ui;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ConsoleHost.AttachToParentConsoleIfPresent();

        var configPath = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var logger = new StructuredConsoleLogger();

        try
        {
            var loader = new HostConfigLoader();
            var config = loader.Load(configPath);
            logger.MinimumLevel = StructuredLogLevelParser.Parse(config.LogLevel);
            logger.OutputFormat = StructuredConsoleLogFormatParser.Parse(config.LogFormat);
            logger.Info("ui_launch", "Launching the Windows host UI.", new Dictionary<string, object?>
            {
                ["configPath"] = configPath,
                ["logLevel"] = logger.MinimumLevel.ToString(),
                ["logFormat"] = logger.OutputFormat.ToString(),
                ["hasAttachedConsole"] = ConsoleHost.HasAttachedConsole
            });

            Application.ThreadException += (_, exceptionArgs) =>
                logger.Error("ui_thread_exception", "Unhandled Windows Forms UI exception.", exceptionArgs.Exception, new Dictionary<string, object?>
                {
                    ["configPath"] = configPath
                });
            AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            {
                if (eventArgs.ExceptionObject is Exception exception)
                {
                    logger.Error("ui_unhandled_exception", "Unhandled application exception reached the AppDomain boundary.", exception, new Dictionary<string, object?>
                    {
                        ["isTerminating"] = eventArgs.IsTerminating
                    });
                }
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(new WindowsHostRuntime(logger, config), logger, configPath));
            logger.Info("ui_exit", "Windows host UI exited normally.", new Dictionary<string, object?>
            {
                ["configPath"] = configPath
            });
            return 0;
        }
        catch (Exception exception)
        {
            logger.Error("ui_launch_failed", "Failed to start the Windows host UI.", exception, new Dictionary<string, object?>
            {
                ["configPath"] = configPath,
                ["hasAttachedConsole"] = ConsoleHost.HasAttachedConsole
            });
            MessageBox.Show(
                $"Failed to start the Windows host UI.\r\n\r\n{exception.Message}",
                "PhlanMic Windows Host",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static partial class ConsoleHost
    {
        private const uint AttachParentProcess = 0xFFFFFFFF;

        public static bool HasAttachedConsole { get; private set; }

        public static void AttachToParentConsoleIfPresent()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            if (!AttachConsole(AttachParentProcess))
            {
                return;
            }

            HasAttachedConsole = true;
            Console.SetOut(CreateWriter(Console.OpenStandardOutput()));
            Console.SetError(CreateWriter(Console.OpenStandardError()));
        }

        private static StreamWriter CreateWriter(Stream stream) =>
            new(stream)
            {
                AutoFlush = true
            };

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool AttachConsole(uint dwProcessId);
    }
}
