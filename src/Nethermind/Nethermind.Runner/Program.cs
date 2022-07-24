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
using Type = System.Type;

namespace Nethermind.Runner;

public class Program
{
    private const string FailureString = "Failure";
    private const string DefaultConfigsDirectory = "configs";
    private const string DefaultConfigFile = "configs/mainnet.cfg";

    //private static readonly ManualResetEventSlim _appClosed = new(true);
    //private static readonly TaskCompletionSource<object?> _cancelKeySource = new();
    private static ILogger _logger = SimpleConsoleLogger.Instance;
    //private static readonly CancellationTokenSource _processCloseCancellationSource = new();
    //private static readonly TaskCompletionSource<object?> _processExit = new();

    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (sender, eventArgs) =>
        {
            ILogger logger = GetCriticalLogger();
            if (eventArgs.ExceptionObject is Exception e)
            {
                logger.Error(FailureString, e);
            }
            else
            {
                logger.Error(FailureString + eventArgs.ExceptionObject);
            }
        };

        try
        {
            BuildCommandLine().Build().Invoke(args);
        }
        catch (AggregateException e)
        {
            ILogger logger = GetCriticalLogger();
            logger.Error(FailureString, e.InnerException);
        }
        catch (Exception e)
        {
            ILogger logger = GetCriticalLogger();
            logger.Error(FailureString, e);
        }
        finally
        {
            NLogManager.Shutdown();
        }
    }

    private static CommandLineBuilder BuildCommandLine()
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

        root.Handler = CommandHandler.Create<InvocationOptions, InvocationContext>(
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

    private static IConfigProvider BuildConfigProvider(InvocationOptions options, InvocationContext context)
    {
        if (options.LoggerConfigurationSource == null)
        {
            _logger.Info($"Loading standard NLog.config file from {"NLog.config".GetApplicationResourcePath()}.");
            Stopwatch stopwatch = Stopwatch.StartNew();
            LogManager.Configuration = new XmlLoggingConfiguration("NLog.config".GetApplicationResourcePath());
            stopwatch.Stop();

            _logger.Info($"NLog.config loaded in {stopwatch.ElapsedMilliseconds}ms.");
        }
        else
        {
            var nLogPath = options.LoggerConfigurationSource;

            _logger.Info($"Loading NLog configuration file from {nLogPath}.");

            try
            {
                LogManager.Configuration = new XmlLoggingConfiguration(nLogPath);
            }
            catch (Exception e)
            {
                _logger.Info($"Failed to load NLog configuration from {nLogPath}. {e}");
            }
        }

        // TODO: dynamically switch log levels from CLI!
        if (options.LogLevel != null)
        {
            NLogConfigurator.ConfigureLogLevels(options.LogLevel);
        }

        ConfigProvider configProvider = new();
        Dictionary<string, string> configArgs = new();

        foreach (var child in context.ParseResult.RootCommandResult.Children)
        {
            if (child is OptionResult result)
            {
                var value = result.GetValueOrDefault<string>();

                if (value != null)
                    configArgs.Add(result.Option.Name, value);
            }
        }

        IConfigSource argsSource = new ArgsConfigSource(configArgs);
        configProvider.AddSource(argsSource);
        configProvider.AddSource(new EnvConfigSource());

        string configDir = options.ConfigurationDirectory ?? DefaultConfigsDirectory;
        string configFilePath = options.Configuration ?? DefaultConfigFile;
        string? configPathVariable = Environment.GetEnvironmentVariable("NETHERMIND_CONFIG");
        if (!string.IsNullOrWhiteSpace(configPathVariable))
        {
            configFilePath = configPathVariable;
        }

        if (!PathUtils.IsExplicitlyRelative(configFilePath))
        {
            configFilePath = configDir == DefaultConfigsDirectory
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

        _logger.Info($"Reading config file from {configFilePath}");
        configProvider.AddSource(new JsonConfigSource(configFilePath));
        configProvider.Initialize();
        var incorrectSettings = configProvider.FindIncorrectSettings();
        if (incorrectSettings.Errors.Count() > 0)
        {
            _logger.Warn($"Incorrect config settings found:{Environment.NewLine}{incorrectSettings.ErrorMsg}");
        }

        _logger.Info("Configuration initialized.");
        return configProvider;
    }

    private static void BuildOptionsFromConfigFiles(Command command, IEnumerable<Type> configTypes)
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

    private static void ConfigureSeqLogger(IConfigProvider configProvider)
    {
        ISeqConfig seqConfig = configProvider.GetConfig<ISeqConfig>();
        if (seqConfig.MinLevel != "Off")
        {
            if (_logger.IsInfo)
                _logger.Info($"Seq Logging enabled on host: {seqConfig.ServerUrl} with level: {seqConfig.MinLevel}");
            NLogConfigurator.ConfigureSeqBufferTarget(seqConfig.ServerUrl, seqConfig.ApiKey, seqConfig.MinLevel);
        }
    }

    //private static void ConsoleOnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    //{
    //    _processCloseCancellationSource.Cancel();
    //    _ = _cancelKeySource.TrySetResult(null);
    //    e.Cancel = true;
    //}

    //private static void CurrentDomainOnProcessExit(object? sender, EventArgs e)
    //{
    //    _processCloseCancellationSource.Cancel();
    //    _processExit.SetResult(null);
    //    _appClosed.Wait();
    //}

    private static ILogger GetCriticalLogger() => new NLogManager("logs.txt").GetClassLogger();

    private static string LoadPluginsDirectory(string[] args)
    {
        var longCommand = "--pluginsDirectory";
        var shortCommand = "-pd";

        string[] GetPluginArgs()
        {
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];

                if (arg == shortCommand || arg == longCommand)
                    return i == args.Length - 1 ? new[] { arg } : new[] { arg, args[i + 1] };
            }

            return Array.Empty<string>();
        }

        var command = new Command("Nethermind.Runner.Plugins");
        var option = new Option<string>(new[] { longCommand, shortCommand }, "Plugins directory");
        var pluginsDirectory = "plugins";

        command.AddOption(option);
        command.SetHandler(context =>
            pluginsDirectory = context.ParseResult.GetValueForOption<string>(option) ?? pluginsDirectory);

        command.Invoke(GetPluginArgs());

        return pluginsDirectory;
    }

    private static void LogMemoryConfiguration()
    {
        if (_logger.IsDebug)
            _logger.Debug($"Server GC           : {System.Runtime.GCSettings.IsServerGC}");
        if (_logger.IsDebug)
            _logger.Debug($"GC latency mode     : {System.Runtime.GCSettings.LatencyMode}");
        if (_logger.IsDebug)
            _logger.Debug($"LOH compaction mode : {System.Runtime.GCSettings.LargeObjectHeapCompactionMode}");
    }

    private static async Task Run(InvocationOptions options, InvocationContext context)
    {
        _logger.Info("Nethermind is starting up");

        //AppDomain.CurrentDomain.ProcessExit += CurrentDomainOnProcessExit;

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

        IConfigProvider configProvider = BuildConfigProvider(options, context);
        IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
        IKeyStoreConfig keyStoreConfig = configProvider.GetConfig<IKeyStoreConfig>();
        IPluginConfig pluginConfig = configProvider.GetConfig<IPluginConfig>();

        pluginLoader.OrderPlugins(pluginConfig);
        Console.Title = initConfig.LogFileName;
        //Console.CancelKeyPress += ConsoleOnCancelKeyPress;

        SetFinalDataDirectory(options.DataDirectory, initConfig, keyStoreConfig);
        NLogManager logManager = new(initConfig.LogFileName, initConfig.LogDirectory, initConfig.LogRules);

        _logger = logManager.GetClassLogger();
        if (_logger.IsDebug) _logger.Debug($"Nethermind version: {ClientVersion.Description}");

        ConfigureSeqLogger(configProvider);
        SetFinalDbPath(options.DatabasePath, initConfig);
        LogMemoryConfiguration();

        EthereumJsonSerializer serializer = new();
        if (_logger.IsDebug) _logger.Debug($"Nethermind config:{Environment.NewLine}{serializer.Serialize(initConfig, true)}{Environment.NewLine}");

        ApiBuilder apiBuilder = new(configProvider, logManager);

        IList<INethermindPlugin> plugins = new List<INethermindPlugin>();
        foreach (Type pluginType in pluginLoader.PluginTypes)
        {
            try
            {
                if (Activator.CreateInstance(pluginType) is INethermindPlugin plugin)
                {
                    plugins.Add(plugin);
                }
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"Failed to create plugin {pluginType.FullName}", e);
            }
        }

        INethermindApi nethermindApi = apiBuilder.Create(plugins.OfType<IConsensusPlugin>());
        ((List<INethermindPlugin>)nethermindApi.Plugins).AddRange(plugins);

        var cancellationToken = context.GetCancellationToken();
        var ethereumRunner = new EthereumRunner(nethermindApi);
        using var terminator = new ManualResetEventSlim();

        try
        {
            await ethereumRunner.Start(cancellationToken).ContinueWith(x =>
            {
                if (x.IsFaulted && _logger.IsError)
                    _logger.Error("Error during ethereum runner start", x.Exception);
            });

            //await Task.WhenAny(_cancelKeySource.Task, _processExit.Task);

            terminator.Wait(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.Info("Nethermind is shutting down... Please wait until all functions are stopped.");

            await ethereumRunner.StopAsync();

            _logger.Info("Nethermind is shut down");

            terminator.Set();
        }
    }

    private static void SetFinalDataDirectory(string? dataDir, IInitConfig initConfig, IKeyStoreConfig keyStoreConfig)
    {
        if (!string.IsNullOrWhiteSpace(dataDir))
        {
            string newDbPath = initConfig.BaseDbPath.GetApplicationResourcePath(dataDir);
            string newKeyStorePath = keyStoreConfig.KeyStoreDirectory.GetApplicationResourcePath(dataDir);
            string newLogDirectory = initConfig.LogDirectory.GetApplicationResourcePath(dataDir);

            if (_logger.IsInfo)
            {
                _logger.Info($"Setting BaseDbPath to: {newDbPath}, from: {initConfig.BaseDbPath}");
                _logger.Info($"Setting KeyStoreDirectory to: {newKeyStorePath}, from: {keyStoreConfig.KeyStoreDirectory}");
                _logger.Info($"Setting LogDirectory to: {newLogDirectory}, from: {initConfig.LogDirectory}");
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

    private static void SetFinalDbPath(string? baseDbPath, IInitConfig initConfig)
    {
        if (!string.IsNullOrWhiteSpace(baseDbPath))
        {
            string newDbPath = initConfig.BaseDbPath.GetApplicationResourcePath(baseDbPath);
            if (_logger.IsDebug) _logger.Debug($"Adding prefix to baseDbPath, new value: {newDbPath}, old value: {initConfig.BaseDbPath}");
            initConfig.BaseDbPath = newDbPath;
        }
        else
        {
            initConfig.BaseDbPath ??= string.Empty.GetApplicationResourcePath("db");
        }
    }

    private static class CliOptions
    {
        public static Option<string> Configuration { get; } = new(new[] { "--config", "-c" }, "Configuration file path");

        public static Option<string> ConfigurationDirectory { get; } = new(new[] { "--configsDirectory", "-cd" });

        public static Option<string> DatabasePath { get; } = new(new[] { "--baseDbPath", "-d" }, "Base database path");

        public static Option<string> DataDirectory { get; } = new(new[] { "--datadir", "-dd" }, "Data directory");

        public static Option<string> LoggerConfigurationSource { get; } =
            new(new[] { "--loggerConfigSource", "-lcs" }, "Path to the NLog configuration file");

        public static Option<string> LogLevel { get; } = new Option<string>(new[] { "--log", "-l" }, "Log level override")
            .FromAmong("OFF", "TRACE", "DEBUG", "INFO", "WARN", "ERROR");

        public static Option<string> PluginsDirectory { get; } =
            new(new[] { "--pluginsDirectory", "-pd" }, "Plugins directory");

        public static Option<bool> Version { get; } = new(new[] { "--version", "-v" }, ClientVersion.Description);
    }

    private record InvocationOptions
    (
        string Configuration,
        string ConfigurationDirectory,
        string DatabasePath,
        string DataDirectory,
        string LoggerConfigurationSource,
        string LogLevel,
        string PluginsDirectory,
        bool Version
    );
}
