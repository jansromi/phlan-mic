using System.Windows.Forms;
using PhlanMic.WindowsHost;

namespace PhlanMic.WindowsHost.Ui;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
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

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(new WindowsHostRuntime(logger, config), configPath));
            return 0;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"Failed to start the Windows host UI.\r\n\r\n{exception.Message}",
                "PhlanMic Windows Host",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }
}
