﻿/*
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
using System.Text;
using Newtonsoft.Json.Linq;

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
            var json = (JObject) JToken.Parse(jsonContent);
            foreach (var moduleEntry in json)
            {
                LoadModule(moduleEntry.Key, (JObject)moduleEntry.Value);
            }
        }

        private void LoadJsonConfig(string configFilePath)
        {
            if (!File.Exists(configFilePath))
            {
                StringBuilder missingConfigFileMessage = new StringBuilder($"Config file {configFilePath} does not exist.");
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
                finally
                {
                    throw new IOException(missingConfigFileMessage.ToString());
                }
            }

            ApplyJsonConfig(File.ReadAllText(configFilePath));
        }

        private void LoadModule(string moduleName, JObject value)
        {
            var configItems = value;
            var itemsDict = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);

            foreach (var configItem in configItems)
            {
                if (!itemsDict.ContainsKey(configItem.Key))
                {
                    itemsDict[configItem.Key] = configItem.Value.ToString();
                }
                else
                {
                    throw new Exception($"Duplicated config value: {configItem.Key}, module: {moduleName}");
                }
            }

            ApplyConfigValues(moduleName, itemsDict);
        }

        Dictionary<string, Dictionary<string, string>> _values = new Dictionary<string, Dictionary<string, string>>(StringComparer.InvariantCultureIgnoreCase);

        Dictionary<string, Dictionary<string, object>> _parsedValues = new Dictionary<string, Dictionary<string, object>>(StringComparer.InvariantCultureIgnoreCase);

        private void ApplyConfigValues(string configModule, Dictionary<string, string> items)
        {
            if (!configModule.EndsWith("Config"))
            {
                configModule = configModule + "Config";
            }
            
            _values[configModule] = items;
            _parsedValues[configModule] = new Dictionary<string, object>(StringComparer.InvariantCultureIgnoreCase);
        }

        private void ParseValue(Type type, string category, string name)
        {
            string valueString = _values[category][name];
            _parsedValues[category][name] = ConfigSourceHelper.ParseValue(type, valueString);
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
            bool isSet = _values.ContainsKey(category) && _values[category].ContainsKey(name);
            return (isSet, isSet ? _values[category][name] : null);
        }
    }
}