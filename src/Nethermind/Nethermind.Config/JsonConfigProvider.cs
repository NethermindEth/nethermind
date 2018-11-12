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
    public class JsonConfigProvider : IConfigProvider
    {
        private IDictionary<Type, IEnumerable<PropertyInfo>> _properties = new Dictionary<Type, IEnumerable<PropertyInfo>>();
        private IDictionary<Type, object> _instances = new Dictionary<Type, object>();

        public JsonConfigProvider()
        {
            Initialize();
        }

        public void ApplyJsonConfig(string jsonContent)
        {
            var json = (JArray) JToken.Parse(jsonContent);
            foreach (var moduleEntry in json)
            {
                LoadModule(moduleEntry);
            }
        }

        public void LoadJsonConfig(string configFilePath)
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

        public T GetConfig<T>() where T : IConfig
        {
            var moduleType = typeof(T);
            if (_instances.ContainsKey(moduleType))
            {
                return (T) _instances[moduleType];
            }

            throw new Exception($"Config type: {moduleType.Name} is not available in ConfigModule.");
        }

        private void Initialize()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.FullName.StartsWith("Nethermind")).ToArray();
            var type = typeof(IConfig);
            var interfaces = assemblies.SelectMany(x => x.GetTypes()).Where(x => type.IsAssignableFrom(x) && x.IsInterface).ToArray();
            for (int i = 0; i < interfaces.Length; i++)
            {
                var module = interfaces[i].Assembly.GetTypes().SingleOrDefault(x => interfaces[i].IsAssignableFrom(x) && x.IsClass);
                if (module != null)
                {
                    _instances[interfaces[i]] = Activator.CreateInstance(module);
                    _properties[interfaces[i]] = module.GetProperties(BindingFlags.Public | BindingFlags.Instance).ToArray();
                }
            }
        }

        private void LoadModule(JToken moduleEntry)
        {
            var configModule = string.Concat("I", (string) moduleEntry["ConfigModule"]);

            var configItems = (JObject) moduleEntry["ConfigItems"];
            var itemsDict = new Dictionary<string, string>();

            foreach (var configItem in configItems)
            {
                if (!itemsDict.ContainsKey(configItem.Key))
                {
                    itemsDict[configItem.Key] = configItem.Value.ToString();
                }
                else
                {
                    throw new Exception($"Duplicated config value: {configItem.Key}, module: {configModule}");
                }
            }

            ApplyConfigValues(configModule, itemsDict);
        }

        private void ApplyConfigValues(string configModule, IDictionary<string, string> items)
        {
            if (!items.Any())
            {
                return;
            }

            var moduleType = _instances.Keys.FirstOrDefault(x => CompareIgnoreCaseTrim(x.Name, $"{configModule}"));
            if (moduleType == null)
            {
                throw new Exception($"Cannot find type with Name: {configModule}");
            }

            var instance = _instances[moduleType];

            foreach (var item in items)
            {
                SetConfigValue(instance, moduleType, item);
            }
        }

        private void SetConfigValue(object configInstance, Type moduleType, KeyValuePair<string, string> item)
        {
            var configProperties = _properties[moduleType];
            var property = configProperties.FirstOrDefault(x => CompareIgnoreCaseTrim(x.Name, item.Key));
            if (property == null)
            {
                throw new Exception($"Invalid configuration. {configInstance.GetType().Name} does not contain property {item.Key}.");
            }

            object value = null;
            var valueType = property.PropertyType;
            if (valueType.IsArray || (valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
            {
                //supports Arrays, e.g int[] and generic IEnumerable<T>, IList<T>
                var itemType = valueType.IsGenericType ? valueType.GetGenericArguments()[0] : valueType.GetElementType();

                //In case of collection of objects (more complex config models) we parse entire collection 
                if (itemType.IsClass && typeof(IConfigModel).IsAssignableFrom(itemType))
                {
                    var objCollection = JsonConvert.DeserializeObject(item.Value, valueType);
                    value = objCollection;
                }
                else
                {
                    var valueItems = item.Value.Split(',').ToArray();
                    var collection = valueType.IsGenericType
                        ? (IList) Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType))
                        : (IList) Activator.CreateInstance(valueType, valueItems.Length);

                    var i = 0;
                    foreach (var valueItem in valueItems)
                    {
                        var itemValue = GetValue(itemType, valueItem, item.Key);
                        if (valueType.IsGenericType)
                        {
                            collection.Add(itemValue);
                        }
                        else
                        {
                            collection[i] = itemValue;
                            i++;
                        }
                    }

                    value = collection;
                }
            }
            else
            {
                value = GetValue(valueType, item.Value, item.Key);
            }

            property.SetValue(configInstance, value);
        }

        private object GetValue(Type valueType, string itemValue, string key)
        {
            if (valueType.IsEnum)
            {
                if (Enum.TryParse(valueType, itemValue, true, out var enumValue))
                {
                    return enumValue;
                }

                throw new Exception($"Cannot parse enum value: {itemValue}, type: {valueType.Name}, key: {key}");
            }

            return Convert.ChangeType(itemValue, valueType);
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
    }
}