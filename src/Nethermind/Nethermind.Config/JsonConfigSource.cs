// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Linq;

namespace Nethermind.Config
{
    public class JsonConfigSource : IConfigSource
    {
        public JsonConfigSource(string configFilePath)
        {
            LoadJsonConfig(configFilePath);
        }

        private void ApplyJsonConfig(string jsonContent)
        {
            try
            {
                using var json = JsonDocument.Parse(jsonContent);
                foreach (var moduleEntry in json.RootElement.EnumerateObject())
                {
                    LoadModule(moduleEntry.Name, moduleEntry.Value);
                }
            }
            catch (Exception e)
            {
                throw new System.Configuration.ConfigurationErrorsException($"Config is not correctly formed JSon. See inner exception for details.", e);
            }
        }

        private void LoadJsonConfig(string configFilePath)
        {
            if (!File.Exists(configFilePath))
            {
                StringBuilder missingConfigFileMessage = new($"Config file {configFilePath} does not exist.");
                try
                {
                    string directory = Path.GetDirectoryName(configFilePath);
                    directory = Path.IsPathRooted(configFilePath)
                        ? directory
                        : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, directory);

                    missingConfigFileMessage.AppendLine().AppendLine($"Search directory: {directory}");

                    string[] configFiles = Directory.GetFiles(directory, "*.cfg");
                    if (configFiles.Length > 0)
                    {
                        missingConfigFileMessage.AppendLine("Found the following config files:");
                        for (int i = 0; i < configFiles.Length; i++)
                        {
                            missingConfigFileMessage.AppendLine($"  * {configFiles[i]}");
                        }
                    }
                }
                catch (Exception)
                {
                    // do nothing - the lines above just give extra info and config is loaded at the beginning so unlikely we have any catastrophic errors here
                }

                throw new IOException(missingConfigFileMessage.ToString());
            }

            ApplyJsonConfig(File.ReadAllText(configFilePath));
        }

        private void LoadModule(string moduleName, JsonElement configItems)
        {
            var itemsDict = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);

            foreach (var configItem in configItems.EnumerateObject())
            {
                var key = configItem.Name;
                if (!itemsDict.ContainsKey(key))
                {
                    var value = configItem.Value;
                    if (value.ValueKind == JsonValueKind.Number)
                    {
                        itemsDict[key] = value.GetInt64().ToString();
                    }
                    else
                    if (value.ValueKind == JsonValueKind.True)
                    {
                        itemsDict[key] = "true";
                    }
                    else if (value.ValueKind == JsonValueKind.False)
                    {
                        itemsDict[key] = "false";
                    }
                    else
                    {
                        itemsDict[key] = configItem.Value.ToString();
                    }
                }
                else
                {
                    throw new Exception($"Duplicated config value: {key}, module: {moduleName}");
                }
            }

            ApplyConfigValues(moduleName, itemsDict);
        }

        private readonly Dictionary<string, Dictionary<string, string>> _values = new(StringComparer.InvariantCultureIgnoreCase);

        private readonly Dictionary<string, Dictionary<string, object>> _parsedValues = new(StringComparer.InvariantCultureIgnoreCase);

        private void ApplyConfigValues(string configModule, Dictionary<string, string> items)
        {
            if (!configModule.EndsWith("Config"))
            {
                configModule += "Config";
            }

            _values[configModule] = items;
            _parsedValues[configModule] = new Dictionary<string, object>(StringComparer.InvariantCultureIgnoreCase);
        }

        private void ParseValue(Type type, string category, string name)
        {
            string valueString = _values[category][name];
            _parsedValues[category][name] = ConfigSourceHelper.ParseValue(type, valueString, category, name);
        }

        public (bool IsSet, object Value) GetValue(Type type, string category, string name)
        {
            (bool isSet, _) = GetRawValue(category, name);
            if (isSet)
            {
                if (!_parsedValues[category].ContainsKey(name))
                {
                    ParseValue(type, category, name);
                }

                return (true, _parsedValues[category][name]);
            }

            return (false, ConfigSourceHelper.GetDefault(type));
        }

        public (bool IsSet, string Value) GetRawValue(string category, string name)
        {
            if (string.IsNullOrEmpty(category) || string.IsNullOrEmpty(name))
            {
                return (false, null);
            }

            bool isSet = _values.ContainsKey(category) && _values[category].ContainsKey(name);
            return (isSet, isSet ? _values[category][name] : null);
        }

        public IEnumerable<(string Category, string Name)> GetConfigKeys()
        {
            return _values.SelectMany(m => m.Value.Keys.Select(n => (m.Key, n)));
        }
    }
}
