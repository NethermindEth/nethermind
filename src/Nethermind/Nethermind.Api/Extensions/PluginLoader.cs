// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Nethermind.Logging;

namespace Nethermind.Api.Extensions;

public class PluginLoader(string pluginPath, IFileSystem fileSystem, ILogger logger, params Type[] embedded) : IPluginLoader
{
    private readonly ILogger _logger = logger;
    private readonly List<Type> _pluginTypes = [];
    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly Type[] _embedded = embedded;
    private readonly string _pluginsDirectory = pluginPath ?? throw new ArgumentNullException(nameof(pluginPath));

    public IEnumerable<Type> PluginTypes => _pluginTypes;

    public void Load()
    {
        if (_logger.IsInfo) _logger.Info("Loading embedded plugins");
        foreach (Type embeddedPlugin in _embedded)
        {
            if (_logger.IsInfo) _logger.Info($"  Found plugin type {embeddedPlugin}");
            _pluginTypes.Add(embeddedPlugin);
        }

        string baseDir = string.Empty.GetApplicationResourcePath();
        string pluginAssembliesDir = _pluginsDirectory.GetApplicationResourcePath();
        if (!_fileSystem.Directory.Exists(pluginAssembliesDir))
        {
            if (_logger.IsWarn) _logger.Warn($"Plugin assemblies folder {pluginAssembliesDir} was not found. Skipping.");
            return;
        }

        string[] assemblies = _fileSystem.Directory.GetFiles(pluginAssembliesDir, "*.dll");
        if (assemblies.Length > 0)
        {
            if (_logger.IsInfo) _logger.Info($"Loading {assemblies.Length} assemblies from {pluginAssembliesDir}");
        }

        foreach (string assemblyName in assemblies)
        {
            string pluginAssembly = _fileSystem.Path.GetFileNameWithoutExtension(assemblyName);

            try
            {
                if (_logger.IsInfo) _logger.Info($"Loading assembly {pluginAssembly}");
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
                            if (_logger.IsInfo) _logger.Info($"  Found plugin type {pluginAssembly}");
                            _pluginTypes.Add(type);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                _logger.Error($"Failed to load plugin {pluginAssembly}", e);
            }
        }
    }

    public void OrderPlugins(IPluginConfig pluginConfig)
    {
        List<string> order = pluginConfig.PluginOrder.Select(s => s.ToLower() + "plugin").ToList();
        _pluginTypes.Sort((f, s) =>
        {
            bool fIsConsensus = typeof(IConsensusPlugin).IsAssignableFrom(f);
            bool sIsConsensus = typeof(IConsensusPlugin).IsAssignableFrom(s);

            // Consensus plugins always at front
            if (fIsConsensus && !sIsConsensus)
            {
                return -1;
            }

            if (sIsConsensus && !fIsConsensus)
            {
                return 1;
            }

            int fPos = order.IndexOf(f.Name.ToLower());
            int sPos = order.IndexOf(s.Name.ToLower());
            if (fPos == -1)
            {
                if (sPos == -1)
                {
                    return f.Name.CompareTo(s.Name);
                }

                return 1;
            }

            if (sPos == -1)
            {
                return -1;
            }

            return fPos.CompareTo(sPos);
        });
    }
}
