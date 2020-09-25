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
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using Nethermind.Logging;

namespace Nethermind.Api.Extensions
{
    public class PluginLoader : IPluginLoader
    {
        private readonly IFileSystem _fileSystem;
        private readonly IPluginManager _pluginManager;
        private readonly string _pluginsDirectory;
        private readonly ILogger _logger;

        public PluginLoader(IPluginManager pluginManager, string pluginPath, IFileSystem fileSystem, ILogManager logManager)
        {
            _logger = logManager.GetClassLogger();
            _pluginManager = pluginManager ?? throw new ArgumentNullException(nameof(pluginManager));
            _pluginsDirectory = pluginPath ?? throw new ArgumentNullException(nameof(pluginPath));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        }

        public void Load()
        {
            string fullPluginsDir = _fileSystem.Path.Combine(AppDomain.CurrentDomain.BaseDirectory!, _pluginsDirectory);
            if (!_fileSystem.Directory.Exists(fullPluginsDir))
            {
                if (_logger.IsWarn) _logger.Warn($"Plugins folder {fullPluginsDir} was not found. Skipping.");
            }
            
            string[] pluginFiles = _fileSystem.Directory.GetFiles(fullPluginsDir).Where(p => p.EndsWith("dll")).ToArray();
            if (pluginFiles.Length > 0)
            {
                if (_logger.IsInfo) _logger.Info($"Loading {pluginFiles.Length} plugins from {fullPluginsDir}");
            }

            foreach (string path in pluginFiles)
            {
                if (_logger.IsInfo) _logger.Warn($"Loading assembly {path}");
                Assembly assembly = Assembly.LoadFile(_fileSystem.Path.Combine(fullPluginsDir, path));
                foreach (Type type in assembly.GetTypes())
                {
                    if (typeof(IPlugin).IsAssignableFrom(type))
                    {
                        _pluginManager.Register(type);
                    }
                }
            }
        }
    }
}