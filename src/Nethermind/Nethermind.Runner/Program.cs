// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using DotNetty.Common;

using Microsoft.Extensions.CommandLineUtils;
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
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Logging.NLog;
using Nethermind.Runner.Ethereum;
using Nethermind.Runner.Ethereum.Api;
using Nethermind.Runner.Logging;
using Nethermind.Seq.Config;
using Nethermind.Serialization.Json;
using Nethermind.UPnP.Plugin;
using NLog;
using NLog.Config;
using ILogger = Nethermind.Logging.ILogger;

namespace Nethermind.Runner
{
    public static class Program
    {
        private const string FailureString = "Failure";
        private const string DefaultConfigsDirectory = "configs";
        private const string DefaultConfigFile = "configs/mainnet.cfg";

        private static ILogger _logger = SimpleConsoleLogger.Instance;

        private static readonly CancellationTokenSource _processCloseCancellationSource = new();
        private static readonly TaskCompletionSource<object?> _cancelKeySource = new();
        private static readonly TaskCompletionSource<object?> _processExit = new();
        private static readonly ManualResetEventSlim _appClosed = new(true);

        public static void Main(string[] args)
        {
#if !DEBUG
            ResourceLeakDetector.Level = ResourceLeakDetector.DetectionLevel.Disabled;
#endif
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
                Run(args);
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

        private static ILogger GetCriticalLogger()
        {
            try
            {
                return new NLogManager("logs.txt").GetClassLogger();
            }
            catch
            {
                if (_logger.IsWarn) _logger.Warn("Critical file logging could not be instantiated! Sticking to console logging till config is loaded.");
                return _logger;
            }
        }

        private static void Run(string[] args)
        {
            _logger.Info("Nethermind starting initialization.");
            _logger.Info($"Client version: {ProductInfo.ClientId}");

            AppDomain.CurrentDomain.ProcessExit += CurrentDomainOnProcessExit;
            AssemblyLoadContext.Default.ResolvingUnmanagedDll += OnResolvingUnmanagedDll;

            GlobalDiagnosticsContext.Set("version", ProductInfo.Version);
            CommandLineApplication app = new() { Name = "Nethermind.Runner" };
            _ = app.HelpOption("-?|-h|--help");
            _ = app.VersionOption("-v|--version", () => ProductInfo.Version, GetProductInfo);

            CommandOption dataDir = app.Option("-dd|--datadir <dataDir>", "Data directory", CommandOptionType.SingleValue);
            CommandOption configFile = app.Option("-c|--config <configFile>", "Config file path", CommandOptionType.SingleValue);
            CommandOption dbBasePath = app.Option("-d|--baseDbPath <baseDbPath>", "Base db path", CommandOptionType.SingleValue);
            CommandOption logLevelOverride = app.Option("-l|--log <logLevel>", "Log level override. Possible values: OFF|TRACE|DEBUG|INFO|WARN|ERROR", CommandOptionType.SingleValue);
            CommandOption configsDirectory = app.Option("-cd|--configsDirectory <configsDirectory>", "Configs directory", CommandOptionType.SingleValue);
            CommandOption loggerConfigSource = app.Option("-lcs|--loggerConfigSource <loggerConfigSource>", "Path to the NLog config file", CommandOptionType.SingleValue);
            _ = app.Option("-pd|--pluginsDirectory <pluginsDirectory>", "plugins directory", CommandOptionType.SingleValue);

            IFileSystem fileSystem = new FileSystem();

            string pluginsDirectoryPath = LoadPluginsDirectory(args);
            PluginLoader pluginLoader = new(pluginsDirectoryPath, fileSystem,
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
            pluginLoader.Load(SimpleConsoleLogManager.Instance);

            BuildOptionsFromConfigFiles(app);

            app.OnExecute(async () =>
            {
                IConfigProvider configProvider = BuildConfigProvider(app, loggerConfigSource, logLevelOverride, configsDirectory, configFile);
                IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
                IKeyStoreConfig keyStoreConfig = configProvider.GetConfig<IKeyStoreConfig>();
                IPluginConfig pluginConfig = configProvider.GetConfig<IPluginConfig>();

                pluginLoader.OrderPlugins(pluginConfig);
                Console.Title = initConfig.LogFileName;
                Console.CancelKeyPress += ConsoleOnCancelKeyPress;

                SetFinalDataDirectory(dataDir.HasValue() ? dataDir.Value() : null, initConfig, keyStoreConfig);
                NLogManager logManager = new(initConfig.LogFileName, initConfig.LogDirectory, initConfig.LogRules);

                _logger = logManager.GetClassLogger();
                ConfigureSeqLogger(configProvider);
                SetFinalDbPath(dbBasePath.HasValue() ? dbBasePath.Value() : null, initConfig);
                LogMemoryConfiguration();

                EthereumJsonSerializer serializer = new();
                if (_logger.IsDebug) _logger.Debug($"Nethermind config:{Environment.NewLine}{serializer.Serialize(initConfig, true)}{Environment.NewLine}");
                if (_logger.IsInfo) _logger.Info($"RocksDb Version: {DbOnTheRocks.GetRocksDbVersion()}");

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

                _appClosed.Reset();
                EthereumRunner ethereumRunner = new(nethermindApi);
                int exitCode = ExitCodes.Ok;
                try
                {
                    await ethereumRunner.Start(_processCloseCancellationSource.Token);

                    _ = await Task.WhenAny(_cancelKeySource.Task, _processExit.Task);
                }
                catch (TaskCanceledException)
                {
                    if (_logger.IsTrace) _logger.Trace("Runner Task was canceled");
                }
                catch (OperationCanceledException)
                {
                    if (_logger.IsTrace) _logger.Trace("Runner operation was canceled");
                }
                catch (Exception e)
                {
                    if (_logger.IsError) _logger.Error("Error during ethereum runner start", e);
                    if (e is IExceptionWithExitCode withExit)
                    {
                        exitCode = withExit.ExitCode;
                    }
                    else
                    {
                        exitCode = ExitCodes.GeneralError;
                    }
                    _processCloseCancellationSource.Cancel();
                }

                _logger.Info("Closing, please wait until all functions are stopped properly...");
                await ethereumRunner.StopAsync();
                _logger.Info("All done, goodbye!");
                _appClosed.Set();

                return exitCode;
            });

            try
            {
                Environment.ExitCode = app.Execute(args);
            }
            catch (Exception e)
            {
                if (e is IExceptionWithExitCode withExit)
                {
                    Environment.ExitCode = withExit.ExitCode;
                }
                else
                {
                    Environment.ExitCode = ExitCodes.GeneralError;
                }
                throw;
            }
            finally
            {
                _appClosed.Wait();
            }
        }

        private static IntPtr OnResolvingUnmanagedDll(Assembly _, string nativeLibraryName)
        {
            const string MacosSnappyPath = "/opt/homebrew/Cellar/snappy";
            var alternativePath = nativeLibraryName switch
            {
                "libdl" => "libdl.so.2",
                "libsnappy" or "snappy" => Directory.Exists(MacosSnappyPath) ?
                    Directory.EnumerateFiles(MacosSnappyPath, "libsnappy.dylib", SearchOption.AllDirectories).FirstOrDefault() : "libsnappy.so.1",
                _ => null
            };

            return alternativePath is null ? IntPtr.Zero : NativeLibrary.Load(alternativePath);
        }

        private static void BuildOptionsFromConfigFiles(CommandLineApplication app)
        {
            Type configurationType = typeof(IConfig);
            IEnumerable<Type> configTypes = TypeDiscovery.FindNethermindTypes(configurationType)
                .Where(ct => ct.IsInterface);

            foreach (Type configType in configTypes.Where(ct => !ct.IsAssignableTo(typeof(INoCategoryConfig))).OrderBy(c => c.Name))
            {
                if (configType is null)
                {
                    continue;
                }

                ConfigCategoryAttribute? typeLevel = configType.GetCustomAttribute<ConfigCategoryAttribute>();

                if (typeLevel is not null && (typeLevel?.DisabledForCli ?? true))
                {
                    continue;
                }

                foreach (PropertyInfo propertyInfo in configType
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .OrderBy(p => p.Name))
                {
                    ConfigItemAttribute? configItemAttribute = propertyInfo.GetCustomAttribute<ConfigItemAttribute>();
                    if (!(configItemAttribute?.DisabledForCli ?? false))
                    {
                        _ = app.Option($"--{configType.Name[1..].Replace("Config", string.Empty)}.{propertyInfo.Name}", $"{(configItemAttribute is null ? "<missing documentation>" : configItemAttribute.Description + $" (DEFAULT: {configItemAttribute.DefaultValue})" ?? "<missing documentation>")}", CommandOptionType.SingleValue);

                    }
                }
            }

            // Create Help Text for environment variables
            Type noCategoryConfig = configTypes.FirstOrDefault(ct => ct.IsAssignableTo(typeof(INoCategoryConfig)));
            if (noCategoryConfig is not null)
            {
                StringBuilder sb = new();
                sb.AppendLine();
                sb.AppendLine("Configurable Environment Variables:");
                foreach (PropertyInfo propertyInfo in noCategoryConfig.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name))
                {
                    ConfigItemAttribute? configItemAttribute = propertyInfo.GetCustomAttribute<ConfigItemAttribute>();
                    if (configItemAttribute is not null && !(string.IsNullOrEmpty(configItemAttribute?.EnvironmentVariable)))
                    {
                        sb.AppendLine($"{configItemAttribute.EnvironmentVariable} - {(string.IsNullOrEmpty(configItemAttribute.Description) ? "<missing documentation>" : configItemAttribute.Description)} (DEFAULT: {configItemAttribute.DefaultValue})");
                    }
                }

                app.ExtendedHelpText = sb.ToString();
            }
        }

        private static string LoadPluginsDirectory(string[] args)
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
                        return i == args.Length - 1 ? new[] { arg } : new[] { arg, args[i + 1] };
                    }
                }

                return Array.Empty<string>();
            }

            CommandLineApplication pluginsApp = new() { Name = "Nethermind.Runner.Plugins" };
            CommandOption pluginsAppDirectory = pluginsApp.Option($"{shortCommand}|{longCommand} <pluginsDirectory>", "plugins directory", CommandOptionType.SingleValue);
            string pluginDirectory = "plugins";
            pluginsApp.OnExecute(() =>
            {
                if (pluginsAppDirectory.HasValue())
                {
                    pluginDirectory = pluginsAppDirectory.Value();
                }

                return 0;
            });
            pluginsApp.Execute(GetPluginArgs());
            return pluginDirectory;
        }

        private static IConfigProvider BuildConfigProvider(
            CommandLineApplication app,
            CommandOption loggerConfigSource,
            CommandOption logLevelOverride,
            CommandOption configsDirectory,
            CommandOption configFile)
        {
            if (loggerConfigSource.HasValue())
            {
                string nLogPath = loggerConfigSource.Value();
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
            else
            {
                _logger.Info($"Loading standard NLog.config file from {"NLog.config".GetApplicationResourcePath()}.");
                Stopwatch stopwatch = Stopwatch.StartNew();
                LogManager.Configuration = new XmlLoggingConfiguration("NLog.config".GetApplicationResourcePath());
                stopwatch.Stop();

                _logger.Info($"NLog.config loaded in {stopwatch.ElapsedMilliseconds}ms.");
            }

            // TODO: dynamically switch log levels from CLI!
            if (logLevelOverride.HasValue())
            {
                NLogConfigurator.ConfigureLogLevels(logLevelOverride);
            }

            ConfigProvider configProvider = new();
            Dictionary<string, string> configArgs = new();
            foreach (CommandOption commandOption in app.Options)
            {
                if (commandOption.HasValue())
                {
                    configArgs.Add(commandOption.LongName, commandOption.Value());
                }
            }

            IConfigSource argsSource = new ArgsConfigSource(configArgs);
            configProvider.AddSource(argsSource);
            configProvider.AddSource(new EnvConfigSource());

            string configDir = configsDirectory.HasValue() ? configsDirectory.Value() : DefaultConfigsDirectory;
            string configFilePath = configFile.HasValue() ? configFile.Value() : DefaultConfigFile;
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
            if (incorrectSettings.Errors.Count > 0)
            {
                _logger.Warn($"Incorrect config settings found:{Environment.NewLine}{incorrectSettings.ErrorMsg}");
            }

            _logger.Info("Configuration initialized.");
            return configProvider;
        }

        private static void CurrentDomainOnProcessExit(object? sender, EventArgs e)
        {
            _processCloseCancellationSource.Cancel();
            _processExit.SetResult(null);
            _appClosed.Wait();
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

        private static void ConsoleOnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            _processCloseCancellationSource.Cancel();
            _ = _cancelKeySource.TrySetResult(null);
            e.Cancel = true;
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
            else
            {
                // Clear it up, otherwise internally it will keep requesting to localhost as `all` target include this.
                NLogConfigurator.ClearSeqTarget();
            }
        }

        private static string GetProductInfo()
        {
            var info = new StringBuilder();

            info
                .Append("Version: ").AppendLine(ProductInfo.Version)
                .Append("Commit: ").AppendLine(ProductInfo.Commit)
                .Append("Build Date: ").AppendLine(ProductInfo.BuildTimestamp.ToString("u"))
                .Append("OS: ")
                    .Append(ProductInfo.OS)
                    .Append(' ')
                    .AppendLine(ProductInfo.OSArchitecture)
                .Append("Runtime: ").AppendLine(ProductInfo.Runtime);

            return info.ToString();
        }
    }
}
