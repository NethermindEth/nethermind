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
            
            var logFileOption = new Option<string?>(
                name: "--log-file",
                description: "Path to log file (if not specified, only console logging is used)",
                getDefaultValue: () => null);
            
            var validateAllBlocksOption = new Option<bool>(
                name: "--validate-all-blocks",
                description: "Enable validation for all blocks, including those where CL doesn't request validation",
                getDefaultValue: () => false);
            
            var feeRecipientOption = new Option<string>(
                name: "--fee-recipient",
                description: "Default fee recipient address for generated payload attributes",
                getDefaultValue: () => "0x8943545177806ed17b9f23f0a21ee5948ecaa776");
            
            var validationModeOption = new Option<ValidationMode>(
                name: "--validation-mode",
                description: "Mode for block validation (Fcu, NewPayload, or Merged)",
                getDefaultValue: () => ValidationMode.NewPayload);
            
            // Create root command with options
            var rootCommand = new RootCommand("Nethermind Engine API Proxy");
            rootCommand.AddOption(executionClientOption);
            rootCommand.AddOption(portOption);
            rootCommand.AddOption(logLevelOption);
            rootCommand.AddOption(logFileOption);
            rootCommand.AddOption(validateAllBlocksOption);
            rootCommand.AddOption(feeRecipientOption);
            rootCommand.AddOption(validationModeOption);
            
            rootCommand.SetHandler(async (string? ecEndpoint, int port, string logLevel, string? logFile, bool validateAllBlocks, string feeRecipient, ValidationMode validationMode) =>
            {
                try
                {
                    // Configure logging
                    var logManager = new NLogManager();
                    
                    // Ensure all logs appear in console output
                    ConfigureConsoleLogging(logLevel);
                    
                    logManager.SetGlobalVariable("logLevel", logLevel);
                    
                    var logger = logManager.GetClassLogger();
                    
                    // Configure file logging only if log file is specified
                    if (!string.IsNullOrWhiteSpace(logFile))
                    {
                        string? logDirectory = Path.GetDirectoryName(logFile);
                        if (string.IsNullOrEmpty(logDirectory))
                        {
                            logDirectory = "logs";
                            logFile = Path.Combine(logDirectory, logFile);
                        }
                        string logFileName = Path.GetFileName(logFile);
                        
                        // Ensure log directory exists
                        if (!Directory.Exists(logDirectory))
                        {
                            Directory.CreateDirectory(logDirectory);
                        }
                        
                        ConfigureFileLogging(logDirectory, logFileName, logLevel);
                        logger.Info($"File logging enabled. Logs will be written to {Path.Combine(logDirectory, logFileName)}");
                    }
                    
                    // Check required parameters
                    if (string.IsNullOrWhiteSpace(ecEndpoint))
                    {
                        logger.Error("Execution Client endpoint is required. Use --ec-endpoint or -e to specify.");
                        Environment.Exit(1);
                    }
                    
                    // Create and configure proxy
                    var config = new ProxyConfig
                    {
                        ExecutionClientEndpoint = ecEndpoint,
                        ListenPort = port,
                        LogLevel = logLevel,
                        LogFile = logFile,
                        ValidateAllBlocks = validateAllBlocks,
                        DefaultFeeRecipient = feeRecipient,
                        ValidationMode = validationMode
                    };
                    
                    logger.Info($"Starting Engine API Proxy with configuration: {config}");
                    
                    // Create and start proxy
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
            }, executionClientOption, portOption, logLevelOption, logFileOption, validateAllBlocksOption, feeRecipientOption, validationModeOption);
            
            return await rootCommand.InvokeAsync(args);
        }
        
        private static void ConfigureConsoleLogging(string logLevel)
        {
            // Access NLog's configuration
            var config = LogManager.Configuration ?? new LoggingConfiguration();
            
            // Create console target
            var consoleTarget = new ColoredConsoleTarget("console")
            {
                Layout = "${longdate}|${level:uppercase=true}|${message} ${exception:format=tostring}"
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
        
        private static void ConfigureFileLogging(string logDirectory, string logFileName, string logLevel)
        {
            // Access NLog's configuration
            var config = LogManager.Configuration ?? new LoggingConfiguration();
            
            // Create file target with JSON format
            var fileTarget = new FileTarget("file")
            {
                FileName = Path.Combine(logDirectory, logFileName),
                Layout = "${longdate}|${level:uppercase=true}|${logger}|${message} ${exception:format=tostring}",
                CreateDirs = true,
                ArchiveFileName = Path.Combine(logDirectory, "archive", "proxy.{#}.logs.txt"),
                ArchiveNumbering = ArchiveNumberingMode.Date,
                ArchiveEvery = FileArchivePeriod.Day,
                MaxArchiveFiles = 7
            };
            
            // Add file target to configuration
            config.AddTarget(fileTarget);
            
            // Add rule for file target with specified log level
            var level = NLog.LogLevel.FromString(logLevel);
            var rule = new LoggingRule("*", level, fileTarget);
            config.LoggingRules.Add(rule);
            
            // Apply configuration
            LogManager.Configuration = config;
        }
    }
}
