//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.CommandLineUtils;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.Clique;
using Nethermind.Consensus.Ethash;
using Nethermind.Core;
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

        public static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (sender, eventArgs) =>
            {
                ILogger logger = new NLogLogger("logs.txt");
                if (eventArgs.ExceptionObject is Exception e)
                    logger.Error(FailureString, e);
                else
                    logger.Error(FailureString + eventArgs.ExceptionObject);
            };

            try
            {
                Run(args);
            }
            catch (AggregateException e)
            {
                ILogger logger = new NLogLogger("logs.txt");
                logger.Error(FailureString, e.InnerException);
            }
            catch (Exception e)
            {
                ILogger logger = new NLogLogger("logs.txt");
                logger.Error(FailureString, e);
            }
            finally
            {
                NLogLogger.Shutdown();
            }
        }

        private static void Run(string[] args)
        {
            _logger.Info("Nethermind starting initialization.");

            AppDomain.CurrentDomain.ProcessExit += CurrentDomainOnProcessExit;
            
            GlobalDiagnosticsContext.Set("version", ClientVersion.Version);
            CommandLineApplication app = new() { Name = "Nethermind.Runner" };
            _ = app.HelpOption("-?|-h|--help");
            _ = app.VersionOption("-v|--version", () => ClientVersion.Version, () => ClientVersion.Description);
            
            CommandOption dataDir = app.Option("-dd|--datadir <dataDir>", "data directory", CommandOptionType.SingleValue);
            CommandOption configFile = app.Option("-c|--config <configFile>", "config file path", CommandOptionType.SingleValue);
            CommandOption dbBasePath = app.Option("-d|--baseDbPath <baseDbPath>", "base db path", CommandOptionType.SingleValue);
            CommandOption logLevelOverride = app.Option("-l|--log <logLevel>", "log level", CommandOptionType.SingleValue);
            CommandOption configsDirectory = app.Option("-cd|--configsDirectory <configsDirectory>", "configs directory", CommandOptionType.SingleValue);
            CommandOption loggerConfigSource = app.Option("-lcs|--loggerConfigSource <loggerConfigSource>", "path to the NLog config file", CommandOptionType.SingleValue);
            _ = app.Option("-pd|--pluginsDirectory <pluginsDirectory>", "plugins directory", CommandOptionType.SingleValue);
            
            IFileSystem fileSystem = new FileSystem();
            
            string pluginsDirectoryPath = LoadPluginsDirectory(args);
            PluginLoader pluginLoader = new(pluginsDirectoryPath, fileSystem, 
                typeof(AuRaPlugin), typeof(CliquePlugin), typeof(EthashPlugin), typeof(NethDevPlugin));

            // leaving here as an example of adding Debug plugin
            // IPluginLoader mevLoader = SinglePluginLoader<MevPlugin>.Instance;
            // CompositePluginLoader pluginLoader = new (pluginLoader, mevLoader);
            pluginLoader.Load(SimpleConsoleLogManager.Instance);

            Type configurationType = typeof(IConfig);
            IEnumerable<Type> configTypes = new TypeDiscovery().FindNethermindTypes(configurationType)
                .Where(ct => ct.IsInterface);

            foreach (Type configType in configTypes.OrderBy(c => c.Name))
            {
                if (configType == null)
                {
                    continue;
                }

                ConfigCategoryAttribute? typeLevel = configType.GetCustomAttribute<ConfigCategoryAttribute>();
                if (typeLevel?.HiddenFromDocs ?? false)
                {
                    continue;
                }

                foreach (PropertyInfo propertyInfo in configType
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .OrderBy(p => p.Name))
                {
                    ConfigItemAttribute? configItemAttribute = propertyInfo.GetCustomAttribute<ConfigItemAttribute>();
                    if (!(configItemAttribute?.HiddenFromDocs ?? false))
                    {
                        _ = app.Option($"--{configType.Name[1..].Replace("Config", string.Empty)}.{propertyInfo.Name}", $"{(configItemAttribute == null ? "<missing documentation>" : configItemAttribute.Description + $" (DEFAULT: {configItemAttribute.DefaultValue})" ?? "<missing documentation>")}", CommandOptionType.SingleValue);
                    }
                }
            }

            ManualResetEventSlim appClosed = new(true);
            app.OnExecute(async () =>
            {
                appClosed.Reset();
                IConfigProvider configProvider = BuildConfigProvider(app, loggerConfigSource, logLevelOverride, configsDirectory, configFile);
                IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
                IKeyStoreConfig keyStoreConfig = configProvider.GetConfig<IKeyStoreConfig>();

                Console.Title = initConfig.LogFileName;
                Console.CancelKeyPress += ConsoleOnCancelKeyPress;

                SetFinalDataDirectory(dataDir.HasValue() ? dataDir.Value() : null, initConfig, keyStoreConfig);
                NLogManager logManager = new(initConfig.LogFileName, initConfig.LogDirectory);

                _logger = logManager.GetClassLogger();
                if (_logger.IsDebug) _logger.Debug($"Nethermind version: {ClientVersion.Description}");

                ConfigureSeqLogger(configProvider);
                SetFinalDbPath(dbBasePath.HasValue() ? dbBasePath.Value() : null, initConfig);
                LogMemoryConfiguration();

                EthereumJsonSerializer serializer = new();
                if (_logger.IsDebug) _logger.Debug($"Nethermind config:{Environment.NewLine}{serializer.Serialize(initConfig, true)}{Environment.NewLine}");

                ApiBuilder apiBuilder = new(configProvider, logManager);
                
                IList<INethermindPlugin> plugins = new List<INethermindPlugin>();
                foreach (Type pluginType in pluginLoader.PluginTypes)
                {
                    if (Activator.CreateInstance(pluginType) is INethermindPlugin plugin)
                    {
                        plugins.Add(plugin);
                    }
                }
                
                INethermindApi nethermindApi = apiBuilder.Create(plugins.OfType<IConsensusPlugin>());
                ((List<INethermindPlugin>)nethermindApi.Plugins).AddRange(plugins);
                
                EthereumRunner ethereumRunner = new(nethermindApi);
                await ethereumRunner.Start(_processCloseCancellationSource.Token).ContinueWith(x =>
                {
                    if (x.IsFaulted && _logger.IsError)
                        _logger.Error("Error during ethereum runner start", x.Exception);
                });

                _ = await Task.WhenAny(_cancelKeySource.Task, _processExit.Task);

                _logger.Info("Closing, please wait until all functions are stopped properly...");
                await ethereumRunner.StopAsync();
                _logger.Info("All done, goodbye!");
                appClosed.Set();

                return 0;
            });

            _ = app.Execute(args);
            appClosed.Wait();
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
                        return i == args.Length - 1 ? new[] {arg} : new[] {arg, args[i + 1]};
                    }
                }

                return Array.Empty<string>();
            }
            
            CommandLineApplication pluginsApp = new() {Name = "Nethermind.Runner.Plugins"};
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
            ILogger logger = SimpleConsoleLogger.Instance;
            if (loggerConfigSource.HasValue())
            {
                string nLogPath = loggerConfigSource.Value();
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
                Stopwatch stopwatch = Stopwatch.StartNew();
                LogManager.Configuration = new XmlLoggingConfiguration("NLog.config".GetApplicationResourcePath());
                stopwatch.Stop();

                logger.Info($"NLog.config loaded in {stopwatch.ElapsedMilliseconds}ms.");
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

            logger.Info($"Reading config file from {configFilePath}");
            configProvider.AddSource(new JsonConfigSource(configFilePath));
            configProvider.Initialize();
            var incorrectSettings = configProvider.FindIncorrectSettings();
            if(incorrectSettings.Errors.Count() > 0)
            {
                logger.Warn($"Incorrect config settings found:{Environment.NewLine}{incorrectSettings.ErrorMsg}");
            }

            logger.Info("Configuration initialized.");
            return configProvider;
        }

        private static void CurrentDomainOnProcessExit(object? sender, EventArgs e)
        {
            _processCloseCancellationSource.Cancel();
            _processExit.SetResult(null);
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
                initConfig.BaseDbPath ??= "".GetApplicationResourcePath("db");
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
                initConfig.BaseDbPath ??= "".GetApplicationResourcePath("db");
                keyStoreConfig.KeyStoreDirectory ??= "".GetApplicationResourcePath("keystore");
                initConfig.LogDirectory ??= "".GetApplicationResourcePath("logs");
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
        }
    }
}
