using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Nethermind.Config
{
    public class JsonConfigProvider : ConfigProvider
    {
        private IDictionary<Type, IEnumerable<PropertyInfo>> _properties;

        public JsonConfigProvider()
        {
            Initialize();
        }

        public void LoadJsonConfig(string configFilePath)
        {
            if (!File.Exists(configFilePath))
            {
                throw new Exception($"Config file does not exist: {configFilePath}");
            }

            using (var reader = File.OpenText(configFilePath))
            {
                var json = (JArray)JToken.ReadFrom(new JsonTextReader(reader));
                foreach (var moduleEntry in json)
                {
                    LoadModule(moduleEntry);
                }
            }
        }

        private void Initialize()
        {
            _properties = new Dictionary<Type, IEnumerable<PropertyInfo>>
            {
                [NetworkConfig.GetType()] = NetworkConfig.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).ToArray(),
                [JsonRpcConfig.GetType()] = JsonRpcConfig.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).ToArray(),
                [KeystoreConfig.GetType()] = KeystoreConfig.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).ToArray()
            };
        }

        private void LoadModule(JToken moduleEntry)
        {
            var configModuleRaw = (string) moduleEntry["ConfigModule"];
            if (!Enum.TryParse(typeof(ConfigModule), configModuleRaw, out var configModule))
            {
                throw new Exception($"Incorrect value for configModuel: {configModuleRaw}");
            }

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
                    throw new Exception($"Duplicated config value: {configItem.Key}, module: {((ConfigModule)configModule).ToString()}");
                }
            }

            ApplyConfigValues((ConfigModule)configModule, itemsDict);
        }

        private void ApplyConfigValues(ConfigModule configModule, IDictionary<string, string> items)
        {
            if (!items.Any())
            {
                return;
            }

            foreach (var item in items)
            {
                switch (configModule)
                {
                    case ConfigModule.Network:
                        SetConfigValue(NetworkConfig, item);
                        break;
                    case ConfigModule.JsonRpc:
                        SetConfigValue(JsonRpcConfig, item);
                        break;
                    case ConfigModule.Keystore:
                        SetConfigValue(KeystoreConfig, item);
                        break;
                    default:
                        throw new Exception($"Unsupported ConfigModule: {configModule}");
                }
            }
        }

        private void SetConfigValue(object configObject, KeyValuePair<string, string> item)
        {
            var configProperties = _properties[configObject.GetType()];
            var property = configProperties.FirstOrDefault(x => CompareIgnoreCaseTrim(x.Name, item.Key));
            if (property == null)
            {
                throw new Exception($"Incorrent config key, no property on {configObject.GetType().Name} config: {item.Key}");
            }

            var valueType = property.PropertyType;
            if (valueType.IsArray || (valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
            {
                //supports Arrays, e.g int[] and generic IEnumerable<T>, IList<T>
                var itemType = valueType.IsGenericType ? valueType.GetGenericArguments()[0]: valueType.GetElementType();

                //In case of collection of objects (more complex config models) we parse entire collection 
                if (itemType.IsClass && typeof(IConfigModel).IsAssignableFrom(itemType))
                {
                    var objCollection = JsonConvert.DeserializeObject(item.Value, valueType);
                    property.SetValue(configObject, objCollection);
                    return;
                }

                var valueItems = item.Value.Split(',').ToArray();
                var collection = valueType.IsGenericType 
                    ? (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType)) 
                    : (IList)Activator.CreateInstance(valueType, valueItems.Length);

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

                property.SetValue(configObject, collection);
                return;
            }
            var value = GetValue(valueType, item.Value, item.Key);
            property.SetValue(configObject, value);
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