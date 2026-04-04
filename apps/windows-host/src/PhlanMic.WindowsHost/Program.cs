using PhlanMic.WindowsHost;

var configPath = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(AppContext.BaseDirectory, "appsettings.json");
var logger = new StructuredConsoleLogger();

try
{
    var loader = new HostConfigLoader();
    var config = loader.Load(configPath);
    logger.MinimumLevel = StructuredLogLevelParser.Parse(config.LogLevel);
    using var cancellation = new CancellationTokenSource();

    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };

    var app = new WindowsHostApp(logger, config);
    await app.RunAsync(cancellation.Token);
    return 0;
}
catch (OperationCanceledException)
{
    logger.Info("host_shutdown", "Host shutdown requested.");
    return 0;
}
catch (Exception exception)
{
    logger.Error("host_start_failed", "Host failed during startup.", exception, new Dictionary<string, object?>
    {
        ["configPath"] = configPath
    });
    return 1;
}
