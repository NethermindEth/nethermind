// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Api.Extensions;

public class PluginLoader(string pluginPath, IFileSystem fileSystem, ILogger logger, params IReadOnlyList<Type> embedded) : IPluginLoader
{
    private readonly List<Type> _pluginTypes = [];
    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly string _pluginsDirectory = pluginPath ?? throw new ArgumentNullException(nameof(pluginPath));

    public IEnumerable<Type> PluginTypes => _pluginTypes;

    public void Load()
    {
        if (logger.IsInfo) logger.Info("Loading embedded plugins");
        foreach (Type embeddedPlugin in embedded)
        {
            if (logger.IsInfo) logger.Info($"  Found plugin type {embeddedPlugin}");
            _pluginTypes.Add(embeddedPlugin);
        }

        string baseDir = string.Empty.GetApplicationResourcePath();
        string pluginAssembliesDir = _pluginsDirectory.GetApplicationResourcePath();
        if (!_fileSystem.Directory.Exists(pluginAssembliesDir))
        {
            if (logger.IsWarn) logger.Warn($"Plugin assemblies folder {pluginAssembliesDir} was not found. Skipping.");
            return;
        }

        string[] assemblies = _fileSystem.Directory.GetFiles(pluginAssembliesDir, "*.dll");
        if (assemblies.Length > 0)
        {
            if (logger.IsInfo) logger.Info($"Loading {assemblies.Length} assemblies from {pluginAssembliesDir}");
        }

        foreach (string assemblyName in assemblies)
        {
            string pluginAssembly = _fileSystem.Path.GetFileNameWithoutExtension(assemblyName);

            try
            {
                if (logger.IsInfo) logger.Info($"Loading assembly {pluginAssembly}");
                string assemblyPath = _fileSystem.Path.Combine(pluginAssembliesDir, assemblyName);
                Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
                AssemblyLoadContext.Default.Resolving += (_, name) =>
                {
                    string fileName = name.Name + ".dll";
                    try
                    {
                        return AssemblyLoadContext.Default.LoadFromAssemblyPath(_fileSystem.Path.Combine(pluginAssembliesDir, fileName));
                    }
                    catch (FileNotFoundException)
                    {
                        return AssemblyLoadContext.Default.LoadFromAssemblyPath(_fileSystem.Path.Combine(baseDir, fileName));
                    }
                };

                foreach (Type type in assembly.GetExportedTypes().Where(t => !t.IsInterface))
                {
                    if (typeof(INethermindPlugin).IsAssignableFrom(type))
                    {
                        if (!PluginTypes.Contains(type))
                        {
                            if (logger.IsInfo) logger.Info($"  Found plugin type {pluginAssembly}");
                            _pluginTypes.Add(type);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                logger.Error($"Failed to load plugin {pluginAssembly}", e);
            }
        }
    }

    public async Task<IList<INethermindPlugin>> LoadPlugins(IConfigProvider configProvider, ChainSpec chainSpec)
    {
        ContainerBuilder builder = new ContainerBuilder()
            .AddSingleton(configProvider)
            .AddSingleton(chainSpec)
            .AddSource(new ConfigRegistrationSource());

        foreach (var pluginType in PluginTypes)
        {
            builder
                .RegisterType(pluginType)
                .ExternallyOwned()
                .As<INethermindPlugin>()
                .SingleInstance();
        }

        await using IContainer container = builder.Build();
        IList<INethermindPlugin> allPlugins = container.Resolve<IList<INethermindPlugin>>();

        List<string> customOrder = configProvider.GetConfig<IPluginConfig>()
            .PluginOrder
            .ToList();

        allPlugins = OrderPlugins(allPlugins, customOrder);

        IList<INethermindPlugin> plugins = new List<INethermindPlugin>();
        if (logger.IsInfo) logger.Info($"Detected {PluginTypes.Count()} plugins");
        foreach (INethermindPlugin plugin in allPlugins)
        {
            try
            {
                if (logger.IsInfo)
                {
                    string pluginName = $"{plugin.Name} by {plugin.Author}";
                    logger.Info($"  {pluginName,-30} {(plugin.Enabled ? "Enabled" : "Disabled")}");
                }
                if (plugin.Enabled)
                {
                    plugins.Add(plugin);
                }
            }
            catch (Exception ex)
            {
                if (logger.IsError) logger.Error($"Failed to load plugin {plugin.Name}", ex);
            }
        }

        if (plugins.OfType<IConsensusPlugin>().Count() > 1)
        {
            throw new InvalidOperationException(
                $"Only one consensus plugin can be enabled at any one time. Enabled plugins: {string.Join(", ", plugins.OfType<IConsensusPlugin>())}"
            );
        }

        return plugins;
    }

    private IList<INethermindPlugin> OrderPlugins(IList<INethermindPlugin> plugins, IReadOnlyList<string> customOrder)
    {
        Dictionary<string, int> priorities = customOrder
            .Select((name, index) => {
                var normalizedName = name.EndsWith("Plugin", StringComparison.OrdinalIgnoreCase)
                    ? name
                    : name + "Plugin";
                return (normalizedName, index);
            })
            .ToDictionary(x => x.normalizedName, x => x.index, StringComparer.OrdinalIgnoreCase);

        return plugins
            .OrderBy(p => priorities.GetValueOrDefault(p.GetType().Name, int.MaxValue))
            .ThenBy(p => p.Priority)
            .ThenBy(p => p.GetType().Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
