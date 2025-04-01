using System.CommandLine;
using Nethermind.EngineApiProxy.Config;
using Nethermind.Logging;
using Nethermind.Logging.NLog;

namespace Nethermind.EngineApiProxy
{
    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            // Create command line options
            var executionClientOption = new Option<string?>(
                name: "--ec-endpoint",
                description: "The URL of the execution client API endpoint");
            executionClientOption.AddAlias("-e");
            
            var portOption = new Option<int>(
                name: "--port",
                description: "The port to listen for consensus client requests",
                getDefaultValue: () => 8551);
            portOption.AddAlias("-p");
            
            var logLevelOption = new Option<string>(
                name: "--log-level",
                description: "Log level (Trace, Debug, Info, Warn, Error)",
                getDefaultValue: () => "Info");
            logLevelOption.AddAlias("-l");
            
            // Create root command with options
            var rootCommand = new RootCommand("Nethermind Engine API Proxy");
            rootCommand.AddOption(executionClientOption);
            rootCommand.AddOption(portOption);
            rootCommand.AddOption(logLevelOption);
            
            rootCommand.SetHandler(async (string? ecEndpoint, int port, string logLevel) =>
            {
                try
                {
                    // Configure logging
                    var logManager = new NLogManager("logs", "proxy.logs.json");
                    logManager.SetGlobalVariable("logLevel", logLevel);
                    
                    var logger = logManager.GetClassLogger();
                    
                    // Check required parameters
                    if (string.IsNullOrWhiteSpace(ecEndpoint))
                    {
                        logger.Error("EC endpoint is required. Use --ec-endpoint or -e to specify.");
                        Environment.Exit(1);
                    }
                    
                    // Create and configure proxy
                    var config = new ProxyConfig
                    {
                        ExecutionClientEndpoint = ecEndpoint,
                        ListenPort = port,
                        LogLevel = logLevel
                    };
                    
                    logger.Info($"Starting Engine API Proxy with configuration: {config}");
                    
                    // Create and start proxy server
                    var proxy = new ProxyServer(config, logManager);
                    await proxy.StartAsync();
                    
                    // Wait for Ctrl+C
                    var cancellationTokenSource = new CancellationTokenSource();
                    Console.CancelKeyPress += (sender, e) =>
                    {
                        e.Cancel = true;
                        cancellationTokenSource.Cancel();
                    };
                    
                    try
                    {
                        await Task.Delay(-1, cancellationTokenSource.Token);
                    }
                    catch (TaskCanceledException)
                    {
                        // Expected when cancellation requested
                    }
                    
                    // Stop proxy server gracefully
                    await proxy.StopAsync();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                    Environment.Exit(1);
                }
            }, executionClientOption, portOption, logLevelOption);
            
            return await rootCommand.InvokeAsync(args);
        }
    }
}
