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
// 

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Nethermind.Logging;

namespace Nethermind.Api.Extensions
{
    public class PluginLoader : IPluginLoader
    {
        private readonly List<Type> _pluginTypes = new();
        private readonly IFileSystem _fileSystem;
        private readonly Type[] _embedded;
        private readonly string _pluginsDirectory;

        public IEnumerable<Type> PluginTypes => _pluginTypes;

        public PluginLoader(string pluginPath, IFileSystem fileSystem, params Type[] embedded)
        {
            _pluginsDirectory = pluginPath ?? throw new ArgumentNullException(nameof(pluginPath));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _embedded = embedded;
        }

        public void Load(ILogManager logManager)
        {
            ILogger logger = logManager.GetClassLogger();
            if (logger.IsInfo) logger.Info("Loading embedded plugins");
            foreach (Type embeddedPlugin in _embedded)
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
                    if (logger.IsInfo) logger.Warn($"Loading assembly {pluginAssembly}");
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
                                if (logger.IsInfo) logger.Warn($"  Found plugin type {pluginAssembly}");
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
            
            // consensus plugins at front
            _pluginTypes.Sort((p1, p2) => 
                typeof(IConsensusPlugin).IsAssignableFrom(p2).CompareTo(typeof(IConsensusPlugin).IsAssignableFrom(p1)));
        }
    }
}
