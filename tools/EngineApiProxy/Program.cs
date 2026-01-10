// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using Nethermind.EngineApiProxy.Config;
using Nethermind.Logging;
using Nethermind.Logging.NLog;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace Nethermind.EngineApiProxy;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Parse command line arguments
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h":
                case "--help":
                    return DisplayHelp();
            }
        }

        // Create command line options
        var executionClientOption = new Option<string?>(
            name: "--ec-endpoint",
            description: "The URL of the execution client API endpoint");
        executionClientOption.AddAlias("-e");

        var consensusClientOption = new Option<string?>(
            name: "--cl-endpoint",
            description: "The URL of the consensus client API endpoint (optional)");
        consensusClientOption.AddAlias("-c");

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
            description: "Mode for block validation (Fcu, NewPayload, Merged, or LH)",
            getDefaultValue: () => ValidationMode.LH);

        var requestTimeoutOption = new Option<int>(
            name: "--request-timeout",
            description: "Timeout in seconds for HTTP requests to EL/CL clients",
            getDefaultValue: () => 60);

        var getPayloadMethodOption = new Option<string>(
            name: "--get-payload-method",
            description: "Engine API method to use when getting payloads for validation (e.g., engine_getPayloadV3, engine_getPayloadV4)",
            getDefaultValue: () => "engine_getPayloadV4");

        var newPayloadMethodOption = new Option<string>(
            name: "--new-payload-method",
            description: "Engine API method to use when sending new payloads for validation (e.g., engine_newPayloadV3, engine_newPayloadV4)",
            getDefaultValue: () => "engine_newPayloadV4");

        // Create root command with options
        var rootCommand = new RootCommand("Nethermind Engine API Proxy");
        rootCommand.AddOption(executionClientOption);
        rootCommand.AddOption(consensusClientOption);
        rootCommand.AddOption(portOption);
        rootCommand.AddOption(logLevelOption);
        rootCommand.AddOption(logFileOption);
        rootCommand.AddOption(validateAllBlocksOption);
        rootCommand.AddOption(feeRecipientOption);
        rootCommand.AddOption(validationModeOption);
        rootCommand.AddOption(requestTimeoutOption);
        rootCommand.AddOption(getPayloadMethodOption);
        rootCommand.AddOption(newPayloadMethodOption);

        rootCommand.SetHandler(async (context) =>
        {
            try
            {
                var ecEndpoint = context.ParseResult.GetValueForOption(executionClientOption);
                var clEndpoint = context.ParseResult.GetValueForOption(consensusClientOption);
                var port = context.ParseResult.GetValueForOption(portOption);
                var logLevel = context.ParseResult.GetValueForOption(logLevelOption) ?? "Info";
                var logFile = context.ParseResult.GetValueForOption(logFileOption);
                var validateAllBlocks = context.ParseResult.GetValueForOption(validateAllBlocksOption);
                var feeRecipient = context.ParseResult.GetValueForOption(feeRecipientOption) ?? "0x8943545177806ed17b9f23f0a21ee5948ecaa776";
                var validationMode = context.ParseResult.GetValueForOption(validationModeOption);
                var requestTimeout = context.ParseResult.GetValueForOption(requestTimeoutOption);
                var getPayloadMethod = context.ParseResult.GetValueForOption(getPayloadMethodOption) ?? "engine_getPayloadV4";
                var newPayloadMethod = context.ParseResult.GetValueForOption(newPayloadMethodOption) ?? "engine_newPayloadV4";

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
                    ConsensusClientEndpoint = clEndpoint,
                    ListenPort = port,
                    LogLevel = logLevel,
                    LogFile = logFile,
                    ValidateAllBlocks = validateAllBlocks,
                    DefaultFeeRecipient = feeRecipient,
                    ValidationMode = validationMode,
                    RequestTimeoutSeconds = requestTimeout,
                    GetPayloadMethod = getPayloadMethod,
                    NewPayloadMethod = newPayloadMethod
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
        });

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

    private static int DisplayHelp()
    {
        Console.WriteLine("Engine API Proxy - Nethermind");
        Console.WriteLine("Usage: EngineApiProxy [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -e, --ec-endpoint <url>           Execution client endpoint URL (required)");
        Console.WriteLine("  -c, --cl-endpoint <url>           Consensus client endpoint URL (optional)");
        Console.WriteLine("  -p, --listen-port <port>          Port to listen on (default: 8551)");
        Console.WriteLine("  -l, --log-level <level>           Logging level (default: Info)");
        Console.WriteLine("  -f, --log-file <path>             Log file path (default: console only)");
        Console.WriteLine("  --validate-all-blocks             Validate all blocks, even without CL request");
        Console.WriteLine("  --fee-recipient <address>         Default fee recipient address");
        Console.WriteLine("  --validation-mode <mode>          Validation mode (Fcu, NewPayload, Merged, LH)");
        Console.WriteLine("  --request-timeout <seconds>       HTTP request timeout in seconds (default: 100)");
        Console.WriteLine("  --get-payload-method <method>     Engine API method for getting payloads (default: engine_getPayloadV4)");
        Console.WriteLine("  --new-payload-method <method>     Engine API method for sending new payloads (default: engine_newPayloadV4)");
        Console.WriteLine("  -h, --help                        Display this help message");
        return 0;
    }
}
