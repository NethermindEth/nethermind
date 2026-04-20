// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using Nethermind.EngineApiProxy.Config;
using Nethermind.Logging.NLog;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace Nethermind.EngineApiProxy;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Create command line options
        Option<string?> executionClientOption = new(
            name: "--ec-endpoint",
            description: "The URL of the execution client API endpoint");
        executionClientOption.AddAlias("-e");

        Option<string?> consensusClientOption = new(
            name: "--cl-endpoint",
            description: "The URL of the consensus client API endpoint (optional)");
        consensusClientOption.AddAlias("-c");

        Option<int> portOption = new(
            name: "--port",
            description: "The port to listen for consensus client requests",
            getDefaultValue: () => 8551);
        portOption.AddAlias("-p");

        Option<string> logLevelOption = new(
            name: "--log-level",
            description: "Log level (Trace, Debug, Info, Warn, Error)",
            getDefaultValue: () => "Info");
        logLevelOption.AddAlias("-l");

        Option<string?> logFileOption = new(
            name: "--log-file",
            description: "Path to log file (if not specified, only console logging is used)",
            getDefaultValue: () => null);

        Option<bool> validateAllBlocksOption = new(
            name: "--validate-all-blocks",
            description: "Enable validation for all blocks, including those where CL doesn't request validation",
            getDefaultValue: () => false);

        Option<string> feeRecipientOption = new(
            name: "--fee-recipient",
            description: "Default fee recipient address for generated payload attributes",
            getDefaultValue: () => "0x8943545177806ed17b9f23f0a21ee5948ecaa776");

        Option<ValidationMode> validationModeOption = new(
            name: "--validation-mode",
            description: "Mode for block validation (ForkChoiceUpdated, NewPayload, Merged, or Lighthouse)",
            getDefaultValue: () => ValidationMode.Lighthouse);

        Option<int> requestTimeoutOption = new(
            name: "--request-timeout",
            description: "Timeout in seconds for HTTP requests to EL/CL clients",
            getDefaultValue: () => 60);

        Option<string> getPayloadMethodOption = new(
            name: "--get-payload-method",
            description: "Engine API method to use when getting payloads for validation (e.g., engine_getPayloadV3, engine_getPayloadV4)",
            getDefaultValue: () => "engine_getPayloadV4");

        Option<string> newPayloadMethodOption = new(
            name: "--new-payload-method",
            description: "Engine API method to use when sending new payloads for validation (e.g., engine_newPayloadV3, engine_newPayloadV4)",
            getDefaultValue: () => "engine_newPayloadV4");

        // Create root command with options
        RootCommand rootCommand = new("Nethermind Engine API Proxy");
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
                string? ecEndpoint = context.ParseResult.GetValueForOption(executionClientOption);
                string? clEndpoint = context.ParseResult.GetValueForOption(consensusClientOption);
                int port = context.ParseResult.GetValueForOption(portOption);
                string logLevel = context.ParseResult.GetValueForOption(logLevelOption) ?? "Info";
                string? logFile = context.ParseResult.GetValueForOption(logFileOption);
                bool validateAllBlocks = context.ParseResult.GetValueForOption(validateAllBlocksOption);
                string feeRecipient = context.ParseResult.GetValueForOption(feeRecipientOption) ?? "0x8943545177806ed17b9f23f0a21ee5948ecaa776";
                ValidationMode validationMode = context.ParseResult.GetValueForOption(validationModeOption);
                int requestTimeout = context.ParseResult.GetValueForOption(requestTimeoutOption);
                string getPayloadMethod = context.ParseResult.GetValueForOption(getPayloadMethodOption) ?? "engine_getPayloadV4";
                string newPayloadMethod = context.ParseResult.GetValueForOption(newPayloadMethodOption) ?? "engine_newPayloadV4";

                // Configure logging
                NLogManager logManager = new();

                // Ensure all logs appear in console output
                ConfigureConsoleLogging(logLevel);

                logManager.SetGlobalVariable("logLevel", logLevel);

                Logging.ILogger logger = logManager.GetClassLogger<Program>();

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
                    context.ExitCode = 1;
                    return;
                }

                // Create and configure proxy
                ProxyConfig config = new()
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
                ProxyServer proxy = new(config, logManager);
                await proxy.StartAsync();

                // Wait for Ctrl+C
                CancellationTokenSource cancellationTokenSource = new();
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
                context.ExitCode = 1;
            }
        });

        return await rootCommand.InvokeAsync(args);
    }

    private static void ConfigureConsoleLogging(string logLevel)
    {
        // Access NLog's configuration
        LoggingConfiguration config = LogManager.Configuration ?? new LoggingConfiguration();

        // Create console target
        ColoredConsoleTarget consoleTarget = new("console")
        {
            Layout = "${longdate}|${level:uppercase=true}|${message} ${exception:format=tostring}"
        };

        // Add console target to configuration
        config.AddTarget(consoleTarget);

        // Add rule for console target with specified log level
        NLog.LogLevel level = NLog.LogLevel.FromString(logLevel);
        LoggingRule rule = new("*", level, consoleTarget);
        config.LoggingRules.Add(rule);

        // Apply configuration
        LogManager.Configuration = config;
    }

    private static void ConfigureFileLogging(string logDirectory, string logFileName, string logLevel)
    {
        // Access NLog's configuration
        LoggingConfiguration config = LogManager.Configuration ?? new LoggingConfiguration();

        // Create file target with JSON format
        FileTarget fileTarget = new("file")
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
        NLog.LogLevel level = NLog.LogLevel.FromString(logLevel);
        LoggingRule rule = new("*", level, fileTarget);
        config.LoggingRules.Add(rule);

        // Apply configuration
        LogManager.Configuration = config;
    }

}
