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
        Option<string?> executionClientOption = new("--ec-endpoint", "-e")
        {
            Description = "The URL of the execution client API endpoint"
        };

        Option<string?> consensusClientOption = new("--cl-endpoint", "-c")
        {
            Description = "The URL of the consensus client API endpoint (optional)"
        };

        Option<int> portOption = new("--port", "-p")
        {
            Description = "The port to listen for consensus client requests",
            DefaultValueFactory = _ => 8551
        };

        Option<string> logLevelOption = new("--log-level", "-l")
        {
            Description = "Log level (Trace, Debug, Info, Warn, Error)",
            DefaultValueFactory = _ => "Info"
        };

        Option<string?> logFileOption = new("--log-file")
        {
            Description = "Path to log file (if not specified, only console logging is used)"
        };

        Option<bool> validateAllBlocksOption = new("--validate-all-blocks")
        {
            Description = "Enable validation for all blocks, including those where CL doesn't request validation",
            DefaultValueFactory = _ => false
        };

        Option<string> feeRecipientOption = new("--fee-recipient")
        {
            Description = "Default fee recipient address for generated payload attributes",
            DefaultValueFactory = _ => "0x8943545177806ed17b9f23f0a21ee5948ecaa776"
        };

        Option<ValidationMode> validationModeOption = new("--validation-mode")
        {
            Description = "Mode for block validation (ForkChoiceUpdated, NewPayload, Merged, or Lighthouse)",
            DefaultValueFactory = _ => ValidationMode.Lighthouse
        };

        Option<int> requestTimeoutOption = new("--request-timeout")
        {
            Description = "Timeout in seconds for HTTP requests to EL/CL clients",
            DefaultValueFactory = _ => 60
        };

        Option<string> getPayloadMethodOption = new("--get-payload-method")
        {
            Description = "Engine API method to use when getting payloads for validation (e.g., engine_getPayloadV3, engine_getPayloadV4)",
            DefaultValueFactory = _ => "engine_getPayloadV4"
        };

        Option<string> newPayloadMethodOption = new("--new-payload-method")
        {
            Description = "Engine API method to use when sending new payloads for validation (e.g., engine_newPayloadV3, engine_newPayloadV4)",
            DefaultValueFactory = _ => "engine_newPayloadV4"
        };

        // Create root command with options
        RootCommand rootCommand = new("Nethermind Engine API Proxy")
        {
            executionClientOption,
            consensusClientOption,
            portOption,
            logLevelOption,
            logFileOption,
            validateAllBlocksOption,
            feeRecipientOption,
            validationModeOption,
            requestTimeoutOption,
            getPayloadMethodOption,
            newPayloadMethodOption,
        };

        rootCommand.SetAction(async (parseResult, ct) =>
        {
            try
            {
                string? ecEndpoint = parseResult.GetValue(executionClientOption);
                string? clEndpoint = parseResult.GetValue(consensusClientOption);
                int port = parseResult.GetValue(portOption);
                string logLevel = parseResult.GetValue(logLevelOption) ?? "Info";
                string? logFile = parseResult.GetValue(logFileOption);
                bool validateAllBlocks = parseResult.GetValue(validateAllBlocksOption);
                string feeRecipient = parseResult.GetValue(feeRecipientOption) ?? "0x8943545177806ed17b9f23f0a21ee5948ecaa776";
                ValidationMode validationMode = parseResult.GetValue(validationModeOption);
                int requestTimeout = parseResult.GetValue(requestTimeoutOption);
                string getPayloadMethod = parseResult.GetValue(getPayloadMethodOption) ?? "engine_getPayloadV4";
                string newPayloadMethod = parseResult.GetValue(newPayloadMethodOption) ?? "engine_newPayloadV4";

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
                    return 1;
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
                CancellationTokenSource cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
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
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        });

        return await rootCommand.Parse(args).InvokeAsync();
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
