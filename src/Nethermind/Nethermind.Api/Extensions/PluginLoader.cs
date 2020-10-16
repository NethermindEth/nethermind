//  Copyright (c) 2018 Demerzel Solutions Limited
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
        private readonly IFileSystem _fileSystem;
        private readonly string _pluginsDirectory;

        public List<Type> PluginTypes = new List<Type>();

        public PluginLoader(string pluginPath, IFileSystem fileSystem)
        {
            _pluginsDirectory = pluginPath ?? throw new ArgumentNullException(nameof(pluginPath));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        }

        public void Load(ILogManager logManager)
        {
            ILogger logger = logManager.GetClassLogger();
            string baseDir = string.Empty.GetApplicationResourcePath();
            string pluginAssembliesDir = _pluginsDirectory.GetApplicationResourcePath();
            if (!_fileSystem.Directory.Exists(pluginAssembliesDir))
            {
                if (logger.IsWarn) logger.Warn($"Plugin assemblies folder {pluginAssembliesDir} was not found. Skipping.");
            }

            string[] assemblies = _fileSystem.Directory.GetFiles(baseDir, "*.dll");
            if (assemblies.Length > 0)
            {
                if (logger.IsInfo) logger.Info($"Loading {assemblies.Length} assemblies from {baseDir}");
            }

            foreach (string assemblyName in assemblies)
            {
                string pluginAssembly = _fileSystem.Path.GetFileNameWithoutExtension(assemblyName);

                try
                {
                    if (logger.IsInfo) logger.Warn($"Loading assembly {pluginAssembly}");
                    string assemblyPath = _fileSystem.Path.Combine(pluginAssembliesDir, assemblyName);
                    Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
                    AssemblyLoadContext.Default.Resolving += (context, name) =>
                    {
                        string fileName = name.Name + ".dll";
                        try
                        {
                            return AssemblyLoadContext.Default.LoadFromAssemblyPath(_fileSystem.Path.Combine(baseDir, fileName));
                        }
                        catch (FileNotFoundException)
                        {
                            return AssemblyLoadContext.Default.LoadFromAssemblyPath(_fileSystem.Path.Combine(_pluginsDirectory, fileName));
                        }
                    };

                    foreach (Type type in assembly.GetExportedTypes().Where(t => !t.IsInterface))
                    {
                        if (typeof(INethermindPlugin).IsAssignableFrom(type))
                        {
                            PluginTypes.Add(type);
                        }
                    }
                }
                catch (Exception e)
                {
                    logger.Error($"Failed to load plugin {pluginAssembly}", e);
                }
            }
        }
    }
}
