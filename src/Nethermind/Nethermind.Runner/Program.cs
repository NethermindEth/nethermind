// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
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
using Nethermind.UPnP.Plugin;
using NLog;
using NLog.Config;
using ILogger = Nethermind.Logging.ILogger;

Console.Title = ProductInfo.Name;
// Increase regex cache size as more added in log coloring matches
Regex.CacheSize = 128;
#if !DEBUG
ResourceLeakDetector.Level = ResourceLeakDetector.DetectionLevel.Disabled;
#endif

ManualResetEventSlim exit = new(true);
ILogger logger = new(SimpleConsoleLogger.Instance);
ProcessExitSource? processExitSource = default;
var unhandledError = "A critical error has occurred";

AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
{
    ILogger criticalLogger = GetCriticalLogger();

    if (e.ExceptionObject is Exception ex)
    {
        criticalLogger.Error($"{unhandledError}.", ex);
    }
    else
    {
        criticalLogger.Error($"{unhandledError}: {e.ExceptionObject}");
    }
};

try
{
    Configure(args);
}
catch (Exception ex)
{
    ILogger criticalLogger = GetCriticalLogger();

    criticalLogger.Error($"{unhandledError}.", ex is AggregateException aex ? aex.InnerException : ex);

    Environment.ExitCode = ExitCodes.GeneralError;
}
finally
{
    NLogManager.Shutdown();
}

void Configure(string[] args)
{
    CliConfiguration cli = ConfigureCli();
    ParseResult parseResult = cli.Parse(args);
    var help = parseResult.GetResult(cli.RootCommand.Options.First())?.GetValueOrDefault<bool>() ?? false;

    ConsoleHelpers.EnableConsoleColorOutput();

    // Don't log if help is requested
    if (!help)
    {
        logger.Info("Nethermind is starting up");
        logger.Info(ProductInfo.ClientId);
    }

    AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
    {
        processExitSource?.Exit(ExitCodes.SigTerm);
        exit.Wait();
    };
    GlobalDiagnosticsContext.Set("version", ProductInfo.Version);

    string pluginsDirectoryPath = LoadPluginsDirectory(args);
    PluginLoader pluginLoader = new(pluginsDirectoryPath, new FileSystem(),
        typeof(AuRaPlugin),
        typeof(CliquePlugin),
        typeof(EthashPlugin),
        typeof(NethDevPlugin),
        typeof(HivePlugin),
        typeof(UPnPPlugin)
    );

    // leaving here as an example of adding Debug plugin
    // IPluginLoader mevLoader = SinglePluginLoader<MevPlugin>.Instance;
    // CompositePluginLoader pluginLoader = new (pluginLoader, mevLoader);

    // Don't log if help is requested
    pluginLoader.Load(help ? NullLogManager.Instance : SimpleConsoleLogManager.Instance);

    TypeDiscovery.Initialize(typeof(INethermindPlugin));

    AddConfigurationOptions(cli.RootCommand);

    cli.RootCommand.SetAction((pr, ct) => Run(pr, pluginLoader, ct));

    try
    {
        Environment.ExitCode = parseResult.Invoke();
    }
    catch (Exception ex)
    {
        Environment.ExitCode = ex is IExceptionWithExitCode withExit
            ? withExit.ExitCode
            : ExitCodes.GeneralError;

        throw;
    }
    finally
    {
        exit.Wait();
    }
}

async Task<int> Run(ParseResult parseResult, PluginLoader pluginLoader, CancellationToken cancellationToken)
{
    processExitSource = new(cancellationToken);
        
    IConfigProvider configProvider = CreateConfigProvider(parseResult);
    IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
    IKeyStoreConfig keyStoreConfig = configProvider.GetConfig<IKeyStoreConfig>();
    ISnapshotConfig snapshotConfig = configProvider.GetConfig<ISnapshotConfig>();
    IPluginConfig pluginConfig = configProvider.GetConfig<IPluginConfig>();

    pluginLoader.OrderPlugins(pluginConfig);

    SetFinalDataDirectory(parseResult.GetResult(BasicOptions.DataDirectory)?.GetValueOrDefault<string>(),
        initConfig, keyStoreConfig, snapshotConfig);

    NLogManager logManager = new(initConfig.LogFileName, initConfig.LogDirectory, initConfig.LogRules);

    logger = logManager.GetClassLogger();
    ConfigureSeqLogger(configProvider);
    SetFinalDbPath(parseResult.GetResult(BasicOptions.DatabasePath)?.GetValueOrDefault<string>(), initConfig);

    if (logger.IsDebug)
    {
        logger.Debug($"""
            Server GC:           {System.Runtime.GCSettings.IsServerGC}
            GC latency mode:     {System.Runtime.GCSettings.LatencyMode}
            LOH compaction mode: {System.Runtime.GCSettings.LargeObjectHeapCompactionMode}
            """);
    }

    EthereumJsonSerializer serializer = new();

    if (logger.IsDebug) logger.Debug($"Nethermind config:{Environment.NewLine}{serializer.Serialize(initConfig, true)}{Environment.NewLine}");
    if (logger.IsInfo) logger.Info($"RocksDB: v{DbOnTheRocks.GetRocksDbVersion()}");

    ApiBuilder apiBuilder = new(configProvider, logManager);
    IList<INethermindPlugin> plugins = [];

    foreach (Type pluginType in pluginLoader.PluginTypes)
    {
        try
        {
            if (Activator.CreateInstance(pluginType) is INethermindPlugin plugin)
            {
                plugins.Add(plugin);
            }
        }
        catch (Exception ex)
        {
            if (logger.IsError) logger.Error($"Failed to create plugin {pluginType.FullName}", ex);
        }
    }

    INethermindApi nethermindApi = apiBuilder.Create(plugins.OfType<IConsensusPlugin>());
    ((List<INethermindPlugin>)nethermindApi.Plugins).AddRange(plugins);
    nethermindApi.ProcessExit = processExitSource;

    //_appClosed.Reset();
    EthereumRunner ethereumRunner = new(nethermindApi);
    try
    {
        await ethereumRunner.Start(processExitSource.Token);
        await processExitSource.ExitTask;
    }
    catch (OperationCanceledException)
    {
        if (logger.IsTrace) logger.Trace("Runner operation was canceled");
    }
    catch (Exception ex)
    {
        if (logger.IsError) logger.Error("Error during ethereum runner start", ex);
        processExitSource.Exit(ex is IExceptionWithExitCode withExit ? withExit.ExitCode : ExitCodes.GeneralError);
    }

    logger.Info("Nethermind is shutting down... Please wait until all activities are stopped.");

    await ethereumRunner.StopAsync();

    logger.Info("Nethermind is shut down");

    exit.Set();

    return processExitSource.ExitCode;
}

void AddConfigurationOptions(CliCommand command)
{
    IEnumerable<Type> configTypes = TypeDiscovery
        .FindNethermindBasedTypes(typeof(IConfig))
        .Where(ct => ct.IsInterface);

    foreach (Type configType in
        configTypes.Where(ct => !ct.IsAssignableTo(typeof(INoCategoryConfig))).OrderBy(c => c.Name))
    {
        if (configType is null)
        {
            continue;
        }

        ConfigCategoryAttribute? typeLevel = configType.GetCustomAttribute<ConfigCategoryAttribute>();

        if (typeLevel is not null && (typeLevel.DisabledForCli || typeLevel.HiddenFromDocs))
        {
            continue;
        }

        foreach (PropertyInfo propertyInfo in
            configType.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name))
        {
            ConfigItemAttribute? configItemAttribute = propertyInfo.GetCustomAttribute<ConfigItemAttribute>();

            if (configItemAttribute?.DisabledForCli == true || configItemAttribute?.HiddenFromDocs == true)
            {
                command.Add(new CliOption<string>($"--{ConfigExtensions.GetCategoryName(configType)}.{propertyInfo.Name}")
                {
                    Description = configItemAttribute?.Description
                });
            }

            if (configItemAttribute?.IsPortOption == true)
            {
                ConfigExtensions.AddPortOptionName(configType, propertyInfo.Name);
            }
        }
    }
}

CliConfiguration ConfigureCli()
{
    CliRootCommand rootCommand =
    [
        BasicOptions.Configuration,
        BasicOptions.ConfigurationDirectory,
        BasicOptions.DatabasePath,
        BasicOptions.DataDirectory,
        BasicOptions.LoggerConfigurationSource,
        BasicOptions.LogLevel,
        BasicOptions.PluginsDirectory
    ];

    var versionOption = (VersionOption)rootCommand.Children.Single(c => c is VersionOption);

    versionOption.Action = CommandHandler.Create<bool>(v => Console.WriteLine($"""
        Version:    {ProductInfo.Version}
        Commit:     {ProductInfo.Commit}
        Build date: {ProductInfo.BuildTimestamp:u}
        Runtime:    {ProductInfo.Runtime}
        OS:         {ProductInfo.OS} {ProductInfo.OSArchitecture}
        """));

    return new(rootCommand) { ProcessTerminationTimeout = Timeout.InfiniteTimeSpan };
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
    if (parseResult.GetResult(BasicOptions.LoggerConfigurationSource)?.GetValueOrDefault<string>() is not null)
    {
        string nLogPath = parseResult.GetResult(BasicOptions.LoggerConfigurationSource)?.GetValueOrDefault<string>();
        logger.Info($"Loading NLog configuration file from {nLogPath}.");

        try
        {
            LogManager.Configuration = new XmlLoggingConfiguration(nLogPath);
        }
        catch (Exception e)
        {
            logger.Info($"Failed to load NLog configuration from {nLogPath}. {e}");
        }
    }
    else
    {
        logger.Info($"Loading standard NLog.config file from {"NLog.config".GetApplicationResourcePath()}.");
        long startTime = Stopwatch.GetTimestamp();
        LogManager.Configuration = new XmlLoggingConfiguration("NLog.config".GetApplicationResourcePath());

        logger.Info($"NLog.config loaded in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms.");
    }

    // TODO: dynamically switch log levels from CLI
    if (parseResult.GetResult(BasicOptions.LogLevel)?.GetValueOrDefault<string>() is not null)
    {
        NLogConfigurator.ConfigureLogLevels(parseResult.GetResult(BasicOptions.LogLevel)?.GetValueOrDefault<string>());
    }

    ConfigProvider configProvider = new();
    Dictionary<string, string> configArgs = [];
    foreach (SymbolResult child in parseResult.RootCommandResult.Children)
    {
        if (child is OptionResult result)
        {
            var value = result.GetValueOrDefault<string>();

            if (value is not null)
                configArgs.Add(result.Option.Name.TrimStart('-'), value);
        }
    }

    IConfigSource argsSource = new ArgsConfigSource(configArgs);
    configProvider.AddSource(argsSource);
    configProvider.AddSource(new EnvConfigSource());

    string configDir = parseResult.GetResult(BasicOptions.ConfigurationDirectory)?.GetValueOrDefault<string>();
    string configFilePath = parseResult.GetResult(BasicOptions.Configuration)?.GetValueOrDefault<string>();
    string? configPathVariable = Environment.GetEnvironmentVariable("NETHERMIND_CONFIG");
    if (!string.IsNullOrWhiteSpace(configPathVariable))
    {
        configFilePath = configPathVariable;
    }

    if (!PathUtils.IsExplicitlyRelative(configFilePath))
    {
        configFilePath = configDir == "configs"
            ? configFilePath.GetApplicationResourcePath()
            : Path.Combine(configDir, string.Concat(configFilePath));
    }

    if (!Path.HasExtension(configFilePath) && !configFilePath.Contains(Path.DirectorySeparatorChar))
    {
        string redirectedConfigPath = Path.Combine(configDir, string.Concat(configFilePath, ".cfg"));
        configFilePath = redirectedConfigPath;
        if (!File.Exists(configFilePath))
        {
            throw new InvalidOperationException($"Configuration: {configFilePath} was not found.");
        }
    }

    if (!Path.HasExtension(configFilePath))
    {
        configFilePath = string.Concat(configFilePath, ".cfg");
    }

    // Fallback to "{executingDirectory}/configs/{configFile}" if "configs" catalog was not specified.
    if (!File.Exists(configFilePath))
    {
        string configName = Path.GetFileName(configFilePath);
        string? configDirectory = Path.GetDirectoryName(configFilePath);
        string redirectedConfigPath = Path.Combine(configDirectory ?? string.Empty, configDir, configName);
        configFilePath = redirectedConfigPath;
        if (!File.Exists(configFilePath))
        {
            throw new InvalidOperationException($"Configuration: {configFilePath} was not found.");
        }
    }

    logger.Info($"Reading config file from {configFilePath}");
    configProvider.AddSource(new JsonConfigSource(configFilePath));
    configProvider.Initialize();
    var incorrectSettings = configProvider.FindIncorrectSettings();
    if (incorrectSettings.Errors.Count > 0)
    {
        logger.Warn($"Incorrect config settings found:{Environment.NewLine}{incorrectSettings.ErrorMsg}");
    }

    logger.Info("Configuration initialized.");
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

string LoadPluginsDirectory(string[] args)
{
    string shortCommand = "-pd";
    string longCommand = "--pluginsDirectory";

    string[] GetPluginArgs()
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == shortCommand || arg == longCommand)
            {
                return i == args.Length - 1 ? [arg] : [arg, args[i + 1]];
            }
        }

        return [];
    }

    CliOption<string> option = new(longCommand, shortCommand);
    CliCommand pluginsApp = new("Nethermind.Runner.Plugins")
    {
        option
    };

    string pluginDirectory = "plugins";

    pluginsApp.SetAction(parseResult =>
    {
        if (parseResult.GetValue(option) is not null)
        {
            pluginDirectory = parseResult.GetValue(option);
        }

        return 0;
    });
    new CliConfiguration(pluginsApp).Invoke(GetPluginArgs());
    return pluginDirectory;
}

void SetFinalDbPath(string? baseDbPath, IInitConfig initConfig)
{
    if (!string.IsNullOrWhiteSpace(baseDbPath))
    {
        string newDbPath = initConfig.BaseDbPath.GetApplicationResourcePath(baseDbPath);
        if (logger.IsDebug) logger.Debug($"Adding prefix to baseDbPath, new value: {newDbPath}, old value: {initConfig.BaseDbPath}");
        initConfig.BaseDbPath = newDbPath;
    }
    else
    {
        initConfig.BaseDbPath ??= string.Empty.GetApplicationResourcePath("db");
    }
}

void SetFinalDataDirectory(string? dataDir, IInitConfig initConfig, IKeyStoreConfig keyStoreConfig, ISnapshotConfig snapshotConfig)
{
    if (!string.IsNullOrWhiteSpace(dataDir))
    {
        string newDbPath = initConfig.BaseDbPath.GetApplicationResourcePath(dataDir);
        string newKeyStorePath = keyStoreConfig.KeyStoreDirectory.GetApplicationResourcePath(dataDir);
        string newLogDirectory = initConfig.LogDirectory.GetApplicationResourcePath(dataDir);
        string newSnapshotPath = snapshotConfig.SnapshotDirectory.GetApplicationResourcePath(dataDir);

        if (logger.IsInfo)
        {
            logger.Info($"Setting BaseDbPath to: {newDbPath}, from: {initConfig.BaseDbPath}");
            logger.Info($"Setting KeyStoreDirectory to: {newKeyStorePath}, from: {keyStoreConfig.KeyStoreDirectory}");
            logger.Info($"Setting LogDirectory to: {newLogDirectory}, from: {initConfig.LogDirectory}");
            if (snapshotConfig.Enabled)
            {
                logger.Info($"Setting SnapshotPath to: {newSnapshotPath}");
            }
        }

        initConfig.BaseDbPath = newDbPath;
        keyStoreConfig.KeyStoreDirectory = newKeyStorePath;
        initConfig.LogDirectory = newLogDirectory;
        snapshotConfig.SnapshotDirectory = newSnapshotPath;
    }
    else
    {
        initConfig.BaseDbPath ??= string.Empty.GetApplicationResourcePath("db");
        keyStoreConfig.KeyStoreDirectory ??= string.Empty.GetApplicationResourcePath("keystore");
        initConfig.LogDirectory ??= string.Empty.GetApplicationResourcePath("logs");
    }
}

static class BasicOptions
{
    public static CliOption<string> Configuration { get; } =
        new("--config", "-c")
        {
            DefaultValueFactory = r => "mainnet",
            Description = "The path to the configuration file or the name (without extension) of any of the configuration files in the configuration directory."
        };

    public static CliOption<string> ConfigurationDirectory { get; } =
        new("--configsDirectory", "-cd")
        {
            DefaultValueFactory = r => "configs",
            Description = "The path to the configuration files directory."
        };

    public static CliOption<string> DatabasePath { get; } = new("--baseDbPath", "-d")
    {
        Description = "The path to the Nethermind database directory."
    };

    public static CliOption<string> DataDirectory { get; } = new("--datadir", "-dd")
    {
        DefaultValueFactory = r => string.Empty.GetApplicationResourcePath(),
        Description = "The path to the Nethermind data directory."
    };

    public static CliOption<string> LoggerConfigurationSource { get; } =
        new("--loggerConfigSource", "-lcs")
        {
            //DefaultValueFactory = r => "NLog.config",
            Description = "The path to the NLog configuration file. "
        };

    public static CliOption<string> LogLevel { get; } = new("--log", "-l")
    {
        DefaultValueFactory = r => "info",
        Description = "Log level (severity). Allowed values: off, trace, debug, info, warn, error."
    };

    public static CliOption<string> PluginsDirectory { get; } =
        new("--pluginsDirectory", "-pd") { Description = "The path to the Nethermind plugins directory." };
}
