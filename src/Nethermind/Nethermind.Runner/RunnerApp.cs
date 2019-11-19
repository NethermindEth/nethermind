/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.CommandLineUtils;
using Nethermind.Config;
using Nethermind.Logging;
using NLog;
using ILogger = Nethermind.Logging.ILogger;

namespace Nethermind.Runner
{
    public class RunnerApp : RunnerAppBase, IRunnerApp
    {
        private const string DefaultConfigsDirectory = "configs";
        private readonly string _defaultConfigFile = Path.Combine(DefaultConfigsDirectory, "mainnet.cfg");

        public RunnerApp(ILogger logger) : base(logger)
        {
        }
 
        protected override (CommandLineApplication, Func<IConfigProvider>, Func<string>) BuildCommandLineApp()
        {
            var pluginsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins");
            if (Directory.Exists(pluginsDirectory))
            {
                var plugins = Directory.GetFiles(pluginsDirectory, "*.dll");
                foreach (var plugin in plugins)
                {
                    var pluginName = plugin.Contains("/") ? plugin.Split("/").Last() : plugin.Split("\\").Last();
                    if (Logger.IsInfo) Logger.Info($"Loading an external assembly: {pluginName}");
                    AssemblyLoadContext.Default.LoadFromAssemblyPath(plugin);
                }
            }

            var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies().ToList();
            loadedAssemblies
                .SelectMany(x => x.GetReferencedAssemblies())
                .Distinct()
                .Where(y => loadedAssemblies.Any((a) => a.FullName == y.FullName) == false)
                .ToList()
                .ForEach(x => loadedAssemblies.Add(AppDomain.CurrentDomain.Load(x)));
            
            var configurationType = typeof(IConfig);
            var configs = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => a.GetTypes())
                .Where(t => configurationType.IsAssignableFrom(t) && !t.IsInterface)
                .ToList();
            
            var app = new CommandLineApplication {Name = "Nethermind.Runner"};
            app.HelpOption("-?|-h|--help");
            var configFile = app.Option("-c|--config <configFile>", "config file path", CommandOptionType.SingleValue);
            var dbBasePath = app.Option("-d|--baseDbPath <baseDbPath>", "base db path", CommandOptionType.SingleValue);
            var logLevelOverride = app.Option("-l|--log <logLevel>", "log level", CommandOptionType.SingleValue);
            
            foreach (Type configType in configs)
            {
                foreach (PropertyInfo propertyInfo in configType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    app.Option($"--{configType.Name.Replace("Config", String.Empty)}.{propertyInfo.Name}", $"{configType.Name}.{propertyInfo.Name}", CommandOptionType.SingleValue);
                }
            }

            IConfigProvider BuildConfigProvider()
            {
                // TODO: dynamically switch log levels from CLI!
                if (logLevelOverride.HasValue())
                {
                    string logLevel = logLevelOverride.Value();
                    NLog.LogLevel nLogLevel = NLog.LogLevel.Info;
                    switch (logLevel.ToUpperInvariant())
                    {
                        case "OFF":
                            nLogLevel = NLog.LogLevel.Off;
                            break;
                        case "ERROR":
                            nLogLevel = NLog.LogLevel.Error;
                            break;
                        case "WARN":
                            nLogLevel = NLog.LogLevel.Warn;
                            break;
                        case "INFO":
                            nLogLevel = NLog.LogLevel.Info;
                            break;
                        case "DEBUG":
                            nLogLevel = NLog.LogLevel.Debug;
                            break;
                        case "TRACE":
                            nLogLevel = NLog.LogLevel.Trace;
                            break;
                    }
                    
                    Console.WriteLine($"Enabling log level override: {logLevel.ToUpperInvariant()}");

                    foreach (var rule in LogManager.Configuration.LoggingRules)
                    {
                        rule.DisableLoggingForLevels(NLog.LogLevel.Trace, nLogLevel);
                        rule.EnableLoggingForLevels(nLogLevel, NLog.LogLevel.Off);
                    }

                    //Call to update existing Loggers created with GetLogger() or //GetCurrentClassLogger()
                    LogManager.ReconfigExistingLoggers();
                }

                ConfigProvider configProvider = new ConfigProvider();
                Dictionary<string, string> args = new Dictionary<string, string>();
                foreach (CommandOption commandOption in app.Options)
                {
                    if (commandOption.HasValue())
                    {
                        args.Add(commandOption.LongName, commandOption.Value());
                    }
                }

                IConfigSource argsSource = new ArgsConfigSource(args);
                configProvider.AddSource(argsSource);
                configProvider.AddSource(new EnvConfigSource());

                string configFilePath = configFile.HasValue() ? configFile.Value() : _defaultConfigFile;
                var configPathVariable = Environment.GetEnvironmentVariable("NETHERMIND_CONFIG");
                if (!string.IsNullOrWhiteSpace(configPathVariable))
                {
                    configFilePath = configPathVariable;
                }

                configFilePath = configFilePath.GetApplicationResourcePath();

                if (!Path.HasExtension(configFilePath) && !configFilePath.Contains(Path.DirectorySeparatorChar))
                {
                    string redirectedConfigPath = Path.Combine(DefaultConfigsDirectory, string.Concat(configFilePath, ".cfg"));
                    Console.WriteLine($"Redirecting config {configFilePath} to {redirectedConfigPath}");
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
                    var configName = Path.GetFileName(configFilePath);
                    var configDirectory = Path.GetDirectoryName(configFilePath);
                    string redirectedConfigPath = Path.Combine(configDirectory, DefaultConfigsDirectory, configName);
                    Console.WriteLine($"Redirecting config {configFilePath} to {redirectedConfigPath}");
                    configFilePath = redirectedConfigPath;
                    if (!File.Exists(configFilePath))
                    {
                        throw new InvalidOperationException($"Configuration: {configFilePath} was not found.");
                    }
                }

                Console.WriteLine($"Reading config file from {configFilePath}");
                configProvider.AddSource(new JsonConfigSource(configFilePath));
                return configProvider;
            }

            string GetBaseDbPath()
            {
                return dbBasePath.HasValue() ? dbBasePath.Value() : null;
            }

            return (app, BuildConfigProvider, GetBaseDbPath);
        }
    }
}