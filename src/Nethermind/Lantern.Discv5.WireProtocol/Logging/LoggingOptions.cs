using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Lantern.Discv5.WireProtocol.Logging;

public static class LoggingOptions
{
    public static ILoggerFactory Default => CreateLoggerFactory();

    private static ILoggerFactory CreateLoggerFactory()
    {
        return LoggerFactory.Create(builder =>
        {
            builder
                .AddFilter("Microsoft", LogLevel.Warning)
                .AddFilter("System", LogLevel.Warning)
                .AddSimpleConsole(options =>
                {
                    options.ColorBehavior = LoggerColorBehavior.Enabled;
                    options.IncludeScopes = false;
                    options.SingleLine = true;
                    options.TimestampFormat = "[HH:mm:ss] ";
                    options.UseUtcTimestamp = true;
                });
        });
    }
}