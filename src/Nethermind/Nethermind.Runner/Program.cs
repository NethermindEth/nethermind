// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
#if !DEBUG
using DotNetty.Common;
#endif
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.Clique;
using Nethermind.Consensus.Ethash;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Db.Rocks;
using Nethermind.Hive;
using Nethermind.Init.Snapshot;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Logging.NLog;
using Nethermind.Runner;
using Nethermind.Runner.Ethereum;
using Nethermind.Runner.Ethereum.Api;
using Nethermind.Runner.Logging;
using Nethermind.Seq.Config;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.UPnP.Plugin;
using NLog;
using NLog.Config;
using ILogger = Nethermind.Logging.ILogger;
using NullLogger = Nethermind.Logging.NullLogger;

Console.Title = ProductInfo.Name;
// Increase regex cache size as more added in log coloring matches
Regex.CacheSize = 128;
#if !DEBUG
ResourceLeakDetector.Level = ResourceLeakDetector.DetectionLevel.Disabled;
#endif
BlocksConfig.SetDefaultExtraDataWithVersion();

ManualResetEventSlim exit = new(true);
ILogger logger = new(SimpleConsoleLogger.Instance);
ProcessExitSource? processExitSource = default;
var unhandledError = "A critical error has occurred";

AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
{
    ILogger criticalLogger = GetCriticalLogger();

    if (e.ExceptionObject is Exception ex)
        criticalLogger.Error($"{unhandledError}.", ex);
    else
        criticalLogger.Error($"{unhandledError}: {e.ExceptionObject}");
};

try
{
    return await ConfigureAsync(args);
}
catch (Exception ex)
{
    ILogger criticalLogger = GetCriticalLogger();

    ex = ex is AggregateException aex ? aex.InnerException : ex;

    criticalLogger.Error($"{unhandledError}.", ex);

    return ex is IExceptionWithExitCode exc ? exc.ExitCode : ExitCodes.GeneralError;
}
finally
{
    NLogManager.Shutdown();
}

async Task<int> ConfigureAsync(string[] args)
{
    CommandLineConfiguration cli = ConfigureCli();
    ParseResult parseResult = cli.Parse(args);
    // Suppress logs if run with `--help` or `--version`
    bool silent = parseResult.CommandResult.Children
        .Any(c => c is OptionResult { Option: HelpOption or VersionOption });

    ConsoleHelpers.EnableConsoleColorOutput();

    ConfigureLogger(parseResult);

    if (!silent)
    {
        logger.Info("Nethermind is starting up");
        logger.Info($"Version: {ProductInfo.Version}");
    }

    AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        processExitSource?.Exit(ExitCodes.SigTerm);
        exit.Wait();
    };
    GlobalDiagnosticsContext.Set("version", ProductInfo.Version);

    PluginLoader pluginLoader = new(
        parseResult.GetValue(BasicOptions.PluginsDirectory) ?? "plugins",
        new FileSystem(),
        silent ? NullLogger.Instance : logger,
        NethermindPlugins.EmbeddedPlugins
    );
    pluginLoader.Load();

    CheckForDeprecatedOptions(parseResult);

    // leaving here as an example of adding Debug plugin
    // IPluginLoader mevLoader = SinglePluginLoader<MevPlugin>.Instance;
    // CompositePluginLoader pluginLoader = new (pluginLoader, mevLoader);

    TypeDiscovery.Initialize(typeof(INethermindPlugin));

    AddConfigurationOptions(cli.RootCommand);

    cli.RootCommand.SetAction((result, token) => RunAsync(result, pluginLoader, token));

    try
    {
        return await cli.InvokeAsync(args);
    }
    finally
    {
        exit.Wait();
    }
}

async Task<int> RunAsync(ParseResult parseResult, PluginLoader pluginLoader, CancellationToken cancellationToken)
{

    IConfigProvider configProvider = CreateConfigProvider(parseResult);
    IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
    IKeyStoreConfig keyStoreConfig = configProvider.GetConfig<IKeyStoreConfig>();
    ISnapshotConfig snapshotConfig = configProvider.GetConfig<ISnapshotConfig>();
    IPluginConfig pluginConfig = configProvider.GetConfig<IPluginConfig>();

    pluginLoader.OrderPlugins(pluginConfig);

    ResolveDataDirectory(parseResult.GetValue(BasicOptions.DataDirectory),
        initConfig, keyStoreConfig, snapshotConfig);

    NLogManager logManager = new(initConfig.LogFileName, initConfig.LogDirectory, initConfig.LogRules);

    logger = logManager.GetClassLogger();

    ConfigureSeqLogger(configProvider);
    ResolveDatabaseDirectory(parseResult.GetValue(BasicOptions.DatabasePath), initConfig);

    logger.Info("Configuration complete");

    EthereumJsonSerializer serializer = new();

    if (logger.IsDebug)
    {
        logger.Debug($"Nethermind configuration:\n{serializer.Serialize(initConfig, true)}");

        logger.Debug($"Server GC:           {GCSettings.IsServerGC}");
        logger.Debug($"GC latency mode:     {GCSettings.LatencyMode}");
        logger.Debug($"LOH compaction mode: {GCSettings.LargeObjectHeapCompactionMode}");
    }

    if (logger.IsInfo) logger.Info($"RocksDB: v{DbOnTheRocks.GetRocksDbVersion()}");

    processExitSource = new(cancellationToken);
    ApiBuilder apiBuilder = new(processExitSource!, configProvider, logManager);
    IList<INethermindPlugin> plugins = await pluginLoader.LoadPlugins(configProvider, apiBuilder.ChainSpec);
    EthereumRunner ethereumRunner = apiBuilder.CreateEthereumRunner(plugins);

    try
    {
        await ethereumRunner.Start(processExitSource.Token);
        await processExitSource.ExitTask;
    }
    catch (OperationCanceledException)
    {
        if (logger.IsTrace) logger.Trace("Nethermind operation was canceled.");
    }
    catch (Exception ex)
    {
        if (logger.IsError) logger.Error(unhandledError, ex);

        processExitSource.Exit(ex is IExceptionWithExitCode withExit ? withExit.ExitCode : ExitCodes.GeneralError);
    }

    logger.Info("Nethermind is shutting down... Please wait until all activities are stopped.");

    await ethereumRunner.StopAsync();

    logger.Info("Nethermind is shut down");

    exit.Set();

    return processExitSource.ExitCode;
}

void AddConfigurationOptions(Command command)
{
    static Option CreateOption<T>(string name, Type configType) =>
        new Option<T>(
            $"--{ConfigExtensions.GetCategoryName(configType)}.{name}",
            $"--{ConfigExtensions.GetCategoryName(configType)}-{name}".ToLowerInvariant());

    IEnumerable<Type> configTypes = TypeDiscovery
        .FindNethermindBasedTypes(typeof(IConfig))
        .Where(ct => ct.IsInterface);

    foreach (Type configType in
        configTypes.Where(ct => !ct.IsAssignableTo(typeof(INoCategoryConfig))).OrderBy(c => c.Name))
    {
        if (configType is null)
            continue;

        ConfigCategoryAttribute? typeLevel = configType.GetCustomAttribute<ConfigCategoryAttribute>();

        if (typeLevel is not null && typeLevel.DisabledForCli)
            continue;

        bool categoryHidden = typeLevel?.HiddenFromDocs == true;

        foreach (PropertyInfo prop in
            configType.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name))
        {
            ConfigItemAttribute? configItemAttribute = prop.GetCustomAttribute<ConfigItemAttribute>();

            if (configItemAttribute?.DisabledForCli != true)
            {
                Option option = prop.PropertyType == typeof(bool)
                    ? CreateOption<bool>(prop.Name, configType)
                    : CreateOption<string>(prop.Name, configType);
                option.Description = configItemAttribute?.Description;
                option.HelpName = "value";
                option.Hidden = categoryHidden || configItemAttribute?.HiddenFromDocs == true;

                command.Add(option);
            }

            if (configItemAttribute?.IsPortOption == true)
                ConfigExtensions.AddPortOptionName(configType, prop.Name);
        }
    }
}

void CheckForDeprecatedOptions(ParseResult parseResult)
{
    Option<string>[] deprecatedOptions =
    [
        BasicOptions.ConfigurationDirectory,
        BasicOptions.DatabasePath,
        BasicOptions.LoggerConfigurationSource,
        BasicOptions.PluginsDirectory
    ];

    foreach (Token token in parseResult.Tokens)
    {
        foreach (Option option in deprecatedOptions)
        {
            if (option.Aliases.Contains(token.Value, StringComparison.Ordinal))
                logger.Warn($"{token} option is deprecated. Use {option.Name} instead.");
        }
    }
}

CommandLineConfiguration ConfigureCli()
{
    RootCommand rootCommand =
    [
        BasicOptions.Configuration,
        BasicOptions.ConfigurationDirectory,
        BasicOptions.DatabasePath,
        BasicOptions.DataDirectory,
        BasicOptions.LoggerConfigurationSource,
        BasicOptions.LogLevel,
        BasicOptions.PluginsDirectory
    ];

    var versionOption = (VersionOption)rootCommand.Children.SingleOrDefault(c => c is VersionOption);

    if (versionOption is not null)
    {
        versionOption.Action = new AsynchronousCommandLineAction(parseResult =>
        {
            parseResult.Configuration.Output.WriteLine($"""
                Version:    {ProductInfo.Version}
                Commit:     {ProductInfo.Commit}
                Build date: {ProductInfo.BuildTimestamp:u}
                Runtime:    {ProductInfo.Runtime}
                Platform:   {ProductInfo.OS} {ProductInfo.OSArchitecture}
                """);

            return ExitCodes.Ok;
        });
    }

    return new(rootCommand)
    {
        EnableDefaultExceptionHandler = false,
        ProcessTerminationTimeout = Timeout.InfiniteTimeSpan
    };
}

void ConfigureLogger(ParseResult parseResult)
{
    string nLogConfig = Path.GetFullPath(
        parseResult.GetValue(BasicOptions.LoggerConfigurationSource)
            ?? "NLog.config".GetApplicationResourcePath());

    try
    {
        LogManager.Configuration = new XmlLoggingConfiguration(nLogConfig);
    }
    catch (Exception ex)
    {
        logger.Error($"Failed to load logging configuration file.", ex);
        return;
    }

    using NLogManager logManager = new();

    logger = logManager.GetClassLogger();

    string? logLevel = parseResult.GetValue(BasicOptions.LogLevel);

    // TODO: dynamically switch log levels from CLI
    if (logLevel is not null)
        NLogConfigurator.ConfigureLogLevels(logLevel);
}

void ConfigureSeqLogger(IConfigProvider configProvider)
{
    ISeqConfig seqConfig = configProvider.GetConfig<ISeqConfig>();

    if (!seqConfig.MinLevel.Equals("Off", StringComparison.Ordinal))
    {
        if (logger.IsInfo)
            logger.Info($"Seq logging is enabled on {seqConfig.ServerUrl} with level of {seqConfig.MinLevel}");

        NLogConfigurator.ConfigureSeqBufferTarget(seqConfig.ServerUrl, seqConfig.ApiKey, seqConfig.MinLevel);
    }
    else
    {
        // Clear it up; otherwise, internally it will keep requesting localhost as `all` target includes this.
        NLogConfigurator.ClearSeqTarget();
    }
}

IConfigProvider CreateConfigProvider(ParseResult parseResult)
{
    ConfigProvider configProvider = new();
    Dictionary<string, string> configArgs = [];

    foreach (SymbolResult child in parseResult.RootCommandResult.Children)
    {
        if (child is OptionResult result)
        {
            var isBoolean = result.Option.GetType().GenericTypeArguments.SingleOrDefault() == typeof(bool);
            var value = isBoolean
                ? result.GetValueOrDefault<bool>().ToString().ToLowerInvariant()
                : result.GetValueOrDefault<string>();

            if (value is not null)
                configArgs.Add(result.Option.Name.TrimStart('-'), value);
        }
    }

    IConfigSource argsSource = new ArgsConfigSource(configArgs);
    configProvider.AddSource(argsSource);
    configProvider.AddSource(new EnvConfigSource());

    string configFile = parseResult.GetValue(BasicOptions.Configuration)
        ?? Environment.GetEnvironmentVariable("NETHERMIND_CONFIG")
        ?? "mainnet";

    // If configFile is not a path, handle it
    if (string.IsNullOrEmpty(Path.GetDirectoryName(configFile)))
    {
        string configsDir = parseResult.GetValue(BasicOptions.ConfigurationDirectory)
            ?? "configs".GetApplicationResourcePath();

        configFile = Path.Join(configsDir, configFile);

        // If the configFile doesn't have an extension, try with supported file extensions
        if (!Path.HasExtension(configFile))
        {
            string? fallback;

            foreach (var ext in new[] { ".json", ".cfg" })
            {
                fallback = $"{configFile}{ext}";

                if (File.Exists(fallback))
                {
                    configFile = fallback;
                    break;
                }
            }
        }
        // For backward compatibility. To be removed in the future.
        else if (Path.GetExtension(configFile).Equals(".cfg", StringComparison.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(configFile)!;

            configFile = $"{configFile[..^4]}.json";

            logger.Warn($"'{name}.cfg' is deprecated. Use '{name}' instead.");
        }
    }

    // Resolve the full path for logging purposes
    configFile = Path.GetFullPath(configFile);

    if (!File.Exists(configFile))
        throw new FileNotFoundException("Configuration file not found.", configFile);

    logger.Info($"Loading configuration from {configFile}");

    configProvider.AddSource(new JsonConfigSource(configFile));
    configProvider.Initialize();

    var (ErrorMsg, Errors) = configProvider.FindIncorrectSettings();

    if (Errors.Any())
        logger.Warn($"Invalid configuration settings:\n{ErrorMsg}");

    return configProvider;
}

ILogger GetCriticalLogger()
{
    try
    {
        return new NLogManager("nethermind.log").GetClassLogger();
    }
    catch
    {
        if (logger.IsWarn) logger.Warn("File logging failed. Using console logging.");

        return logger;
    }
}

void ResolveDatabaseDirectory(string? path, IInitConfig initConfig)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        initConfig.BaseDbPath ??= string.Empty.GetApplicationResourcePath("db");
    }
    else
    {
        string dbPath = initConfig.BaseDbPath.GetApplicationResourcePath(path);

        if (logger.IsDebug) logger.Debug($"{nameof(initConfig.BaseDbPath)}: {Path.GetFullPath(dbPath)}");

        initConfig.BaseDbPath = dbPath;
    }
}

void ResolveDataDirectory(string? path, IInitConfig initConfig, IKeyStoreConfig keyStoreConfig, ISnapshotConfig snapshotConfig)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        initConfig.BaseDbPath ??= string.Empty.GetApplicationResourcePath("db");
        keyStoreConfig.KeyStoreDirectory ??= string.Empty.GetApplicationResourcePath("keystore");
        initConfig.LogDirectory ??= string.Empty.GetApplicationResourcePath("logs");
    }
    else
    {
        string newDbPath = initConfig.BaseDbPath.GetApplicationResourcePath(path);
        string newKeyStorePath = keyStoreConfig.KeyStoreDirectory.GetApplicationResourcePath(path);
        string newLogDirectory = initConfig.LogDirectory.GetApplicationResourcePath(path);
        string newSnapshotPath = snapshotConfig.SnapshotDirectory.GetApplicationResourcePath(path);

        if (logger.IsInfo)
        {
            logger.Info($"{nameof(initConfig.BaseDbPath)}: {Path.GetFullPath(newDbPath)}");
            logger.Info($"{nameof(keyStoreConfig.KeyStoreDirectory)}: {Path.GetFullPath(newKeyStorePath)}");
            logger.Info($"{nameof(initConfig.LogDirectory)}: {Path.GetFullPath(newLogDirectory)}");

            if (snapshotConfig.Enabled)
                logger.Info($"{nameof(snapshotConfig.SnapshotDirectory)}: {Path.GetFullPath(newSnapshotPath)}");
        }

        initConfig.BaseDbPath = newDbPath;
        keyStoreConfig.KeyStoreDirectory = newKeyStorePath;
        initConfig.LogDirectory = newLogDirectory;
        snapshotConfig.SnapshotDirectory = newSnapshotPath;
    }
}

static class BasicOptions
{
    public static Option<string> Configuration { get; } =
        new("--config", "-c")
        {
            Description = "The path to the configuration file or the file name (also without extension) of any of the configuration files in the configuration files directory.",
            HelpName = "network or file name"
        };

    public static Option<string> ConfigurationDirectory { get; } =
        new("--configs-dir", "--configsDirectory", "-cd")
        {
            Description = "The path to the configuration files directory.",
            HelpName = "path"
        };

    public static Option<string> DatabasePath { get; } = new("--db-dir", "--baseDbPath", "-d")
    {
        Description = "The path to the Nethermind database directory.",
        HelpName = "path"
    };

    public static Option<string> DataDirectory { get; } = new("--data-dir", "--datadir", "-dd")
    {
        Description = "The path to the Nethermind data directory.",
        HelpName = "path"
    };

    public static Option<string> LoggerConfigurationSource { get; } =
        new("--logger-config", "--loggerConfigSource", "-lcs")
        {
            Description = "The path to the logging configuration file.",
            HelpName = "path"
        };

    public static Option<string> LogLevel { get; } = new("--log", "-l")
    {
        Description = "Log level (severity). Allowed values: off, trace, debug, info, warn, error.",
        HelpName = "level"
    };

    public static Option<string> PluginsDirectory { get; } =
        new("--plugins-dir", "--pluginsDirectory", "-pd")
        {
            Description = "The path to the Nethermind plugins directory.",
            HelpName = "path"
        };
}

class AsynchronousCommandLineAction(Func<ParseResult, int> action) : SynchronousCommandLineAction
{
    private readonly Func<ParseResult, int> _action = action ?? throw new ArgumentNullException(nameof(action));

    /// <inheritdoc />
    public override int Invoke(ParseResult parseResult) => _action(parseResult);
}
