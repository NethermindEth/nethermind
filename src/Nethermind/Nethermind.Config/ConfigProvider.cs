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
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Nethermind.Config
{
    public class JsonConfigProvider : IConfigProvider
    {
        private ConfigProvider _provider = new ConfigProvider();

        public JsonConfigProvider(string jsonConfigFile)
        {
            _provider.AddSource(new JsonConfigSource(jsonConfigFile));
        }
        
        public T GetConfig<T>() where T : IConfig
        {
            return _provider.GetConfig<T>();
        }

        public void AddSource(IConfigSource configSource)
        {
            _provider.AddSource(configSource);
        }
    }
    
    public class ConfigProvider : IConfigProvider
    {
        private IDictionary<Type, object> _instances = new Dictionary<Type, object>();
        
        private List<IConfigSource> _configSource = new List<IConfigSource>();

        public ConfigProvider()
        {
            Initialize();
        }
        
        public T GetConfig<T>() where T : IConfig
        {
            if (!_instances.ContainsKey(typeof(T)))
            {
                T config = (T)Activator.CreateInstance(_implementations[typeof(T)]);
                foreach (PropertyInfo propertyInfo in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    IConfigEntry configEntry = (IConfigEntry)propertyInfo.GetValue(config);
                    for (int i = 0; i < _configSource.Count; i++)
                    {
                        (bool isSet, object value) = _configSource[i].GetValue(configEntry.Type, configEntry.Name, configEntry.Category);
                        if (isSet)
                        {
                            configEntry.SetValue(value);
                            break;
                        }
                    }
                }
            }

            return (T)_instances[typeof(T)];
        }

        public void AddSource(IConfigSource configSource)
        {
            _configSource.Add(configSource);
        }
        
        private Dictionary<Type, Type> _implementations = new Dictionary<Type, Type>();
        
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
                    _implementations[interfaces[i]] = module;
                }
            }
        }
    }
}