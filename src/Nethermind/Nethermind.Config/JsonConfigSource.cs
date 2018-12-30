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
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
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
            var json = (JArray) JToken.Parse(jsonContent);
            foreach (var moduleEntry in json)
            {
                LoadModule(moduleEntry);
            }
        }

        private void LoadJsonConfig(string configFilePath)
        {
            if (!File.Exists(configFilePath))
            {
                StringBuilder missingConfigFileMessage = new StringBuilder($"Config file does not exist {configFilePath}");
                try
                {
                    missingConfigFileMessage.AppendLine().AppendLine("Did you mean any of these:");
                    string[] configFiles = Directory.GetFiles(Path.GetDirectoryName(configFilePath), "*.cfg");
                    for (int i = 0; i < configFiles.Length; i++)
                    {
                        missingConfigFileMessage.AppendLine($"  * {configFiles[i]}");
                    }
                }
                catch (Exception)
                {
                    // do nothing - the lines above just give extra info and config is loaded at the beginning so unlikely we have any catastrophic errors here
                }
                finally
                {
                    throw new Exception(missingConfigFileMessage.ToString());
                }
            }

            ApplyJsonConfig(File.ReadAllText(configFilePath));
        }

        private void LoadModule(JToken moduleEntry)
        {
            var entryName = (string) moduleEntry["ConfigModule"];
            var configModule = string.Concat("I", entryName);

            var configItems = (JObject) moduleEntry["ConfigItems"];
            var itemsDict = new Dictionary<string, string>();

            foreach (var configItem in configItems)
            {
                if (!itemsDict.ContainsKey(configItem.Key))
                {
                    itemsDict[configItem.Key] = GetItemValue(entryName, configItem.Key, configItem.Value.ToString());
                }
                else
                {
                    throw new Exception($"Duplicated config value: {configItem.Key}, module: {configModule}");
                }
            }

            ApplyConfigValues(configModule, itemsDict);
        }

        Dictionary<string, Dictionary<string, string>> _values = new Dictionary<string, Dictionary<string, string>>();

        Dictionary<string, Dictionary<string, object>> _parsedValues = new Dictionary<string, Dictionary<string, object>>();

        private void ApplyConfigValues(string configModule, Dictionary<string, string> items)
        {
            _values[configModule] = items;
            _parsedValues[configModule] = new Dictionary<string, object>();
        }

        private void ParseValue(Type type, string category, string name)
        {
            string valueString = _values[category][name];
            _parsedValues[category][name] = ConfigSourceHelper.ParseValue(type, valueString);
        }

        private string GetItemValue(string configModule, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return value;
            }

            var variableName = $"NETHERMIND_{configModule.ToUpperInvariant()}_{key.ToUpperInvariant()}";
            var variableValue = Environment.GetEnvironmentVariable(variableName);

            return string.IsNullOrWhiteSpace(variableValue) ? value : variableValue;
        }

        private bool CompareIgnoreCaseTrim(string value1, string value2)
        {
            if (string.IsNullOrEmpty(value1) && string.IsNullOrEmpty(value2))
            {
                return true;
            }

            if (string.IsNullOrEmpty(value1) || string.IsNullOrEmpty(value2))
            {
                return false;
            }

            return string.Compare(value1.Trim(), value2.Trim(), StringComparison.CurrentCultureIgnoreCase) == 0;
        }

        public (bool IsSet, object Value) GetValue(Type type, string category, string name)
        {
            if (!_values.ContainsKey(category) || !_values[category].ContainsKey(name))
            {
                return (false, ConfigSourceHelper.GetDefault(type));
            }

            if (!_parsedValues[category].ContainsKey(name))
            {
                ParseValue(type, category, name);
            }
            
            return (true, _parsedValues[category][name]);
        }
    }
}