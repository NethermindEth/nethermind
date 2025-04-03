using System.CommandLine;
using Nethermind.EngineApiProxy.Config;
using Nethermind.Logging;
using Nethermind.Logging.NLog;
using NLog;
using NLog.Config;
using NLog.Targets;

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
            
            var validateAllBlocksOption = new Option<bool>(
                name: "--validate-all-blocks",
                description: "Enable validation for all blocks, including those where CL doesn't request validation",
                getDefaultValue: () => false);
            
            var feeRecipientOption = new Option<string>(
                name: "--fee-recipient",
                description: "Default fee recipient address for generated payload attributes",
                getDefaultValue: () => "0x8943545177806ed17b9f23f0a21ee5948ecaa776");
            
            // Create root command with options
            var rootCommand = new RootCommand("Nethermind Engine API Proxy");
            rootCommand.AddOption(executionClientOption);
            rootCommand.AddOption(portOption);
            rootCommand.AddOption(logLevelOption);
            rootCommand.AddOption(validateAllBlocksOption);
            rootCommand.AddOption(feeRecipientOption);
            
            rootCommand.SetHandler(async (string? ecEndpoint, int port, string logLevel, bool validateAllBlocks, string feeRecipient) =>
            {
                try
                {
                    // Configure logging
                    var logManager = new NLogManager("logs", "proxy.logs.json");
                    
                    // Ensure all logs also appear in console output
                    ConfigureConsoleLogging(logLevel);
                    
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
                        LogLevel = logLevel,
                        ValidateAllBlocks = validateAllBlocks,
                        DefaultFeeRecipient = feeRecipient
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
            }, executionClientOption, portOption, logLevelOption, validateAllBlocksOption, feeRecipientOption);
            
            return await rootCommand.InvokeAsync(args);
        }
        
        private static void ConfigureConsoleLogging(string logLevel)
        {
            // Access NLog's configuration
            var config = LogManager.Configuration ?? new LoggingConfiguration();
            
            // Create console target
            var consoleTarget = new ColoredConsoleTarget("console")
            {
                Layout = "${longdate}|${level:uppercase=true}|${logger}|${message} ${exception:format=tostring}"
            };
            
            // Add console target to configuration
            config.AddTarget(consoleTarget);
            
            // Add rule for console target with specified log level
            var level = NLog.LogLevel.FromString(logLevel);
            var rule = new LoggingRule("*", level, consoleTarget);
            config.LoggingRules.Add(rule);
            
            // Apply configuration
            LogManager.Configuration = config;
        }
    }
}
