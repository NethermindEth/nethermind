using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Providers.Plugins.Yaml
{
    public class YamlNdmPluginLoader : INdmPluginLoader
    {
        private readonly string _directory;
        private readonly INdmPluginBuilder _builder;
        private readonly ILogManager _logManager;
        private readonly ILogger _logger;

        public YamlNdmPluginLoader(string directory, INdmPluginBuilder builder, ILogManager logManager)
        {
            _directory = Path.Combine(PathUtils.ExecutingDirectory, directory);
            _builder = builder;
            _logManager = logManager;
            _logger = logManager.GetClassLogger();
        }

        public IEnumerable<INdmPlugin> Load()
        {
            if(_logger.IsInfo) _logger.Info($"Plugins directory: {_directory}");

            if (!Directory.Exists(_directory))
            {
                if(_logger.IsError) _logger.Error($"Plugins directory does not exist at {_directory}");
                return Enumerable.Empty<INdmPlugin>();
            }

            var plugins = new List<INdmPlugin>();
            var files = Directory.GetFiles(_directory, "*.yml").OrderBy(f => f);
            
            if(files.Count() == 0)
            {
                if(_logger.IsInfo) _logger.Info($"No plugin files found in {_directory}");
            }

            foreach (var file in files)
            {
                var fileName = file.Contains("/") ? file.Split("/").Last() : file.Split("\\").Last();
                if (_logger.IsInfo) _logger.Info($"Loading NDM plugin: {fileName}");
                
                try
                {
                    var description = File.ReadAllText(file);
                    if (string.IsNullOrWhiteSpace(description))
                    {
                        continue;
                    }

                    var plugin = _builder.Build(description);
                    if (plugin is null)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(plugin.Name))
                    {
                        if (_logger.IsError) _logger.Error("NDM plugin name cannot be empty");
                        continue;
                    }
                    
                    if (string.IsNullOrWhiteSpace(plugin.Type))
                    {
                        if (_logger.IsError) _logger.Error($"NDM plugin: {plugin.Name} has empty type");
                        continue;
                    }
                    
                    plugins.Add(plugin);
                }
                catch (Exception ex)
                {
                    if (_logger.IsError) _logger.Error($"There was an error when initializing NDM plugin: {fileName}", ex);
                }
            }

            return plugins;
        }
    }
}