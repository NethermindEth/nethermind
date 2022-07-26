//  Copyright (c) 2022 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.CommandLine.NamingConventionBinder;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.Clique;
using Nethermind.Consensus.Ethash;
using Nethermind.Core;
using Nethermind.Hive;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Logging.NLog;
using Nethermind.Runner.Ethereum;
using Nethermind.Runner.Ethereum.Api;
using Nethermind.Runner.Logging;
using Nethermind.Seq.Config;
using Nethermind.Serialization.Json;
using NLog;
using NLog.Config;
using ILogger = Nethermind.Logging.ILogger;

var genericError = "Unhandled error:";
var logger = (ILogger)SimpleConsoleLogger.Instance;

AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
{
    var logger = GetCriticalLogger();

    if (e.ExceptionObject is Exception ex)
        logger.Error(genericError, ex);
    else
        logger.Error($"{genericError} {e.ExceptionObject}");
};

try
{
    BuildCommandLine().Build().Invoke(args);
}
catch (AggregateException ex)
{
    logger = GetCriticalLogger();

    logger.Error(genericError, ex.InnerException);
}
catch (Exception ex)
{
    logger = GetCriticalLogger();

    logger.Error(genericError, ex);
}
finally
{
    NLogManager.Shutdown();
}

CommandLineBuilder BuildCommandLine()
{
    var root = new RootCommand("Nethermind")
    {
        CliOptions.Configuration,
        CliOptions.ConfigurationDirectory,
        CliOptions.DatabasePath,
        CliOptions.DataDirectory,
        CliOptions.LoggerConfigurationSource,
        CliOptions.LogLevel,
        CliOptions.PluginsDirectory,
        CliOptions.Version
    };

    root.Handler = CommandHandler.Create<InvocationValues, InvocationContext>(
        async (options, context) => await Run(options, context));

    var configTypes = new TypeDiscovery()
        .FindNethermindTypes(typeof(IConfig))
        .Where(ct => ct.IsInterface);

    BuildOptionsFromConfigFiles(root, configTypes);

    return new CommandLineBuilder(root)
        .AddMiddleware(async (context, next) =>
        {
            if (context.ParseResult.FindResultFor(CliOptions.Version) is { })
                context.Console.WriteLine(ClientVersion.Description);
            else
                await next(context);
        })
        .UseHelp(context =>
        {
            // Create Help Text for environment variables
            var noCategoryConfig = configTypes.FirstOrDefault(ct => ct.IsAssignableTo(typeof(INoCategoryConfig)));

            if (noCategoryConfig != null)
            {
                var help = new StringBuilder()
                    .AppendLine()
                    .AppendLine("Configurable environment variables:");
                var props = noCategoryConfig
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .OrderBy(p => p.Name);

                foreach (var prop in props)
                {
                    var itemAttr = prop.GetCustomAttribute<ConfigItemAttribute>();

                    if (itemAttr != null && !string.IsNullOrEmpty(itemAttr?.EnvironmentVariable))
                        help
                            .Append(itemAttr.EnvironmentVariable)
                            .Append(" - ")
                            .AppendLine(string.IsNullOrEmpty(itemAttr.Description)
                                ? "<missing documentation>"
                                : $"{itemAttr.Description} (DEFAULT: {itemAttr.DefaultValue})");
                }

                context.HelpBuilder.CustomizeLayout(_ =>
                    HelpBuilder.Default.GetLayout().Append(e => e.Output.WriteLine(help.ToString())));
            }
        })
        .UseEnvironmentVariableDirective()
        .UseParseDirective()
        .UseSuggestDirective()
        .RegisterWithDotnetSuggest()
        .UseTypoCorrections()
        .UseParseErrorReporting()
        .UseExceptionHandler()
        .CancelOnProcessTermination();
}

IConfigProvider BuildConfigProvider(InvocationValues options, InvocationContext context)
{
    if (options.LoggerConfigSource == null)
    {
        logger.Info($"Loading standard NLog.config file from {"NLog.config".GetApplicationResourcePath()}.");

        var stopwatch = Stopwatch.StartNew();

        LogManager.Configuration = new XmlLoggingConfiguration("NLog.config".GetApplicationResourcePath());

        stopwatch.Stop();

        logger.Info($"NLog.config loaded in {stopwatch.ElapsedMilliseconds}ms.");
    }
    else
    {
        var nLogPath = options.LoggerConfigSource;

        logger.Info($"Loading NLog configuration file from {nLogPath}.");

        try
        {
            LogManager.Configuration = new XmlLoggingConfiguration(nLogPath);
        }
        catch (Exception ex)
        {
            logger.Info($"Failed to load NLog configuration from {nLogPath}. {ex}");
        }
    }

    // TODO: dynamically switch log levels from CLI!
    if (options.Log != null)
        NLogConfigurator.ConfigureLogLevels(options.Log);

    var configProvider = new ConfigProvider();
    var configArgs = new Dictionary<string, string>();

    foreach (var child in context.ParseResult.RootCommandResult.Children)
        if (child is OptionResult result)
        {
            var value = result.GetValueOrDefault<string>();

            if (value != null)
                configArgs.Add(result.Option.Name, value);
        }

    var argsSource = new ArgsConfigSource(configArgs);

    configProvider.AddSource(argsSource);
    configProvider.AddSource(new EnvConfigSource());

    var configFilePath = options.Config;
    var configPathVariable = Environment.GetEnvironmentVariable("NETHERMIND_CONFIG");

    if (!string.IsNullOrWhiteSpace(configPathVariable))
        configFilePath = configPathVariable;

    // If the config file path is rooted, don't handle it
    if (!Path.IsPathRooted(configFilePath))
    {
        // If the config file path is file name only, combine with `configsDirectory`
        // and add an extension if needed
        if (string.IsNullOrEmpty(Path.GetDirectoryName(configFilePath)))
            configFilePath = Path.Combine(
                options.ConfigsDirectory,
                Path.HasExtension(configFilePath) ? configFilePath : $"{configFilePath}.cfg");

        // If the resulting path is still not rooted, combine with the current directory
        if (!Path.IsPathRooted(configFilePath))
            configFilePath = Path.Combine(PathUtils.ExecutingDirectory, configFilePath);
    }

    configFilePath = Path.GetFullPath(configFilePath);

    if (!File.Exists(configFilePath))
        throw new FileNotFoundException("Configuration not found", configFilePath);

    logger.Info($"Reading config file from {configFilePath}");

    configProvider.AddSource(new JsonConfigSource(configFilePath));
    configProvider.Initialize();

    var incorrectSettings = configProvider.FindIncorrectSettings();

    if (incorrectSettings.Errors.Any())
        logger.Warn($"Incorrect config settings found:{Environment.NewLine}{incorrectSettings.ErrorMsg}");

    logger.Info("Configuration initialized.");

    return configProvider;
}

void BuildOptionsFromConfigFiles(Command command, IEnumerable<Type> configTypes)
{
    configTypes = configTypes
        .Where(ct => !ct.IsAssignableTo(typeof(INoCategoryConfig)))
        .OrderBy(c => c.Name);

    foreach (var type in configTypes)
    {
        if (type == null)
            continue;

        var catAttr = type.GetCustomAttribute<ConfigCategoryAttribute>();

        if (catAttr != null && (catAttr?.DisabledForCli ?? true))
            continue;

        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name);

        foreach (var prop in props)
        {
            var itemAttr = prop.GetCustomAttribute<ConfigItemAttribute>();

            if (!(itemAttr?.DisabledForCli ?? false))
                command.AddOption(new Option<string>(
                    $"--{type.Name[1..].Replace("Config", null)}.{prop.Name}",
                    itemAttr == null
                        ? "<missing documentation>"
                        : $"{itemAttr.Description} (DEFAULT: {itemAttr.DefaultValue})"));
        }
    }
}

void ConfigureSeqLogger(IConfigProvider configProvider)
{
    var config = configProvider.GetConfig<ISeqConfig>();

    if (!config.MinLevel.Equals("Off", StringComparison.Ordinal))
    {
        if (logger.IsInfo) logger.Info($"Seq Logging enabled on host: {config.ServerUrl} with level: {config.MinLevel}");

        NLogConfigurator.ConfigureSeqBufferTarget(config.ServerUrl, config.ApiKey, config.MinLevel);
    }
}

ILogger GetCriticalLogger() => new NLogManager("logs.txt").GetClassLogger();

string LoadPluginsDirectory(string[] args)
{
    var longCommand = "--pluginsDirectory";
    var shortCommand = "-pd";

    string[] GetPluginArgs()
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg.Equals(shortCommand, StringComparison.Ordinal) ||
                arg.Equals(longCommand, StringComparison.Ordinal))
                return i == args.Length - 1 ? new[] { arg } : new[] { arg, args[i + 1] };
        }

        return Array.Empty<string>();
    }

    var command = new Command("Nethermind.Runner.Plugins");
    var option = new Option<string>(new[] { longCommand, shortCommand }, "Plugins directory");
    var dir = "plugins";

    command.AddOption(option);
    command.SetHandler(context => dir = context.ParseResult.GetValueForOption<string>(option) ?? dir);
    command.Invoke(GetPluginArgs());

    return dir;
}

void LogMemoryConfiguration()
{
    if (!logger.IsDebug)
        return;

    logger.Debug($"Server GC:           {System.Runtime.GCSettings.IsServerGC}");
    logger.Debug($"GC latency mode:     {System.Runtime.GCSettings.LatencyMode}");
    logger.Debug($"LOH compaction mode: {System.Runtime.GCSettings.LargeObjectHeapCompactionMode}");
}

async Task Run(InvocationValues options, InvocationContext context)
{
    logger.Info("Nethermind is starting up");

    GlobalDiagnosticsContext.Set("version", ClientVersion.Version);

    var fileSystem = new FileSystem();
    var pluginsDirectoryPath = LoadPluginsDirectory(context.ParseResult.Tokens.Select(t => t.Value).ToArray());
    var pluginLoader = new PluginLoader(
        pluginsDirectoryPath,
        fileSystem,
        typeof(AuRaPlugin),
        typeof(CliquePlugin),
        typeof(EthashPlugin),
        typeof(NethDevPlugin),
        typeof(HivePlugin));

    // leaving here as an example of adding Debug plugin
    // IPluginLoader mevLoader = SinglePluginLoader<MevPlugin>.Instance;
    // CompositePluginLoader pluginLoader = new (pluginLoader, mevLoader);
    pluginLoader.Load(SimpleConsoleLogManager.Instance);

    var configProvider = BuildConfigProvider(options, context);
    var initConfig = configProvider.GetConfig<IInitConfig>();
    var keyStoreConfig = configProvider.GetConfig<IKeyStoreConfig>();
    var pluginConfig = configProvider.GetConfig<IPluginConfig>();

    pluginLoader.OrderPlugins(pluginConfig);

    SetFinalDataDirectory(options.Datadir, initConfig, keyStoreConfig);

    var logManager = new NLogManager(initConfig.LogFileName, initConfig.LogDirectory, initConfig.LogRules);

    logger = logManager.GetClassLogger();

    if (logger.IsDebug) logger.Debug($"Nethermind version: {ClientVersion.Description}");

    ConfigureSeqLogger(configProvider);
    SetFinalDbPath(options.BaseDbPath, initConfig);
    LogMemoryConfiguration();

    var serializer = new EthereumJsonSerializer();

    if (logger.IsDebug) logger.Debug($"Nethermind config:{Environment.NewLine}{serializer.Serialize(initConfig, true)}{Environment.NewLine}");

    var apiBuilder = new ApiBuilder(configProvider, logManager);
    var plugins = new List<INethermindPlugin>();

    foreach (var type in pluginLoader.PluginTypes)
        try
        {
            if (Activator.CreateInstance(type) is INethermindPlugin plugin)
                plugins.Add(plugin);
        }
        catch (Exception ex)
        {
            if (logger.IsError) logger.Error($"Failed to create plugin {type.FullName}", ex);
        }

    var nethermindApi = apiBuilder.Create(plugins.OfType<IConsensusPlugin>());

    // TODO API design violation. Consider using Linq Concat() instead.
    ((List<INethermindPlugin>)nethermindApi.Plugins).AddRange(plugins);

    var cancellationToken = context.GetCancellationToken();
    var ethereumRunner = new EthereumRunner(nethermindApi);
    using var terminator = new ManualResetEventSlim();

    try
    {
        await ethereumRunner.Start(cancellationToken).ContinueWith(t =>
        {
            if (t.IsFaulted && logger.IsError)
                logger.Error("Error during ethereum runner start", t.Exception);
        });

        terminator.Wait(cancellationToken);
    }
    catch (OperationCanceledException)
    {
        logger.Info("Nethermind is shutting down... Please wait until all functions are stopped.");

        await ethereumRunner.StopAsync();

        logger.Info("Nethermind is shut down");

        terminator.Set();
    }
}

void SetFinalDataDirectory(string? dataDir, IInitConfig initConfig, IKeyStoreConfig keyStoreConfig)
{
    if (!string.IsNullOrWhiteSpace(dataDir))
    {
        var newDbPath = initConfig.BaseDbPath.GetApplicationResourcePath(dataDir);
        var newKeyStorePath = keyStoreConfig.KeyStoreDirectory.GetApplicationResourcePath(dataDir);
        var newLogDirectory = initConfig.LogDirectory.GetApplicationResourcePath(dataDir);

        if (logger.IsInfo)
        {
            logger.Info($"Setting BaseDbPath to: {newDbPath}, from: {initConfig.BaseDbPath}");
            logger.Info($"Setting KeyStoreDirectory to: {newKeyStorePath}, from: {keyStoreConfig.KeyStoreDirectory}");
            logger.Info($"Setting LogDirectory to: {newLogDirectory}, from: {initConfig.LogDirectory}");
        }

        initConfig.BaseDbPath = newDbPath;
        keyStoreConfig.KeyStoreDirectory = newKeyStorePath;
        initConfig.LogDirectory = newLogDirectory;
    }
    else
    {
        initConfig.BaseDbPath ??= string.Empty.GetApplicationResourcePath("db");
        keyStoreConfig.KeyStoreDirectory ??= string.Empty.GetApplicationResourcePath("keystore");
        initConfig.LogDirectory ??= string.Empty.GetApplicationResourcePath("logs");
    }
}

void SetFinalDbPath(string? baseDbPath, IInitConfig initConfig)
{
    if (!string.IsNullOrWhiteSpace(baseDbPath))
    {
        var newDbPath = initConfig.BaseDbPath.GetApplicationResourcePath(baseDbPath);

        if (logger.IsDebug) logger.Debug($"Adding prefix to baseDbPath, new value: {newDbPath}, old value: {initConfig.BaseDbPath}");

        initConfig.BaseDbPath = newDbPath;
    }
    else
        initConfig.BaseDbPath ??= string.Empty.GetApplicationResourcePath("db");
}

class CliOptions
{
    public static Option<string> Configuration { get; } =
        new(new[] { "--config", "-c" }, () => "configs/mainnet.cfg", "Configuration file path");

    public static Option<string> ConfigurationDirectory { get; } =
        new(new[] { "--configsDirectory", "-cd" }, () => "configs", "Configuration file directory");

    public static Option<string> DatabasePath { get; } = new(new[] { "--baseDbPath", "-d" }, "Base database path");

    public static Option<string> DataDirectory { get; } = new(new[] { "--datadir", "-dd" }, "Data directory");

    public static Option<string> LoggerConfigurationSource { get; } =
        new(new[] { "--loggerConfigSource", "-lcs" }, "Path to the NLog configuration file");

    public static Option<string> LogLevel { get; } = new Option<string>(new[] { "--log", "-l" }, "Log level override")
        .FromAmong("OFF", "TRACE", "DEBUG", "INFO", "WARN", "ERROR");

    public static Option<string> PluginsDirectory { get; } =
        new(new[] { "--pluginsDirectory", "-pd" }, "Plugins directory");

    public static Option<bool> Version { get; } = new(new[] { "--version", "-v" }, "Show version information");
}

record InvocationValues
(
    string Config,
    string ConfigsDirectory,
    string BaseDbPath,
    string Datadir,
    string LoggerConfigSource,
    string Log,
    string PluginsDirectory,
    bool Version
);
