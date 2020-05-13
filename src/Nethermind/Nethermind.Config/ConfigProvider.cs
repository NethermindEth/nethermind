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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Nethermind.Config
{
    public class ConfigProvider : IConfigProvider
    {
        private readonly ConcurrentDictionary<Type, object> _instances = new ConcurrentDictionary<Type, object>();
        
        private readonly List<IConfigSource> _configSource = new List<IConfigSource>();

        public T GetConfig<T>() where T : IConfig
        {
            if (!_instances.ContainsKey(typeof(T)))
            {
                if (!_implementations.ContainsKey(typeof(T)))
                {
                    Initialize();
                }
                
                T config = (T)Activator.CreateInstance(_implementations[typeof(T)]);
                _instances[typeof(T)] = config;
                foreach (PropertyInfo propertyInfo in config.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    for (int i = 0; i < _configSource.Count; i++)
                    {
                        string category = config.GetType().Name;
                        string name = propertyInfo.Name;
                        (bool isSet, object value) = _configSource[i].GetValue(propertyInfo.PropertyType, category, name);
                        if (isSet)
                        {
                            try
                            {
                                propertyInfo.SetValue(config, value);
                            }
                            catch (Exception e)
                            {
                                throw new InvalidOperationException($"Cannot set value of {category}.{name}", e);
                            }
                            
                            break;
                        }
                    }
                }
            }

            return (T)_instances[typeof(T)];
        }

        public object GetRawValue(string category, string name)
        {
            for (int i = 0; i < _configSource.Count; i++)
            {
                (bool isSet, string value) = _configSource[i].GetRawValue(category, name);
                if (isSet)
                {
                    return value;
                }
            }

            return Categories.ContainsKey(category) ? Categories[category].GetType()
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .SingleOrDefault(p => string.Equals(p.Name, name, StringComparison.InvariantCultureIgnoreCase))
                ?.GetValue(Categories[category]) : null;
        }

        public void AddSource(IConfigSource configSource)
        {
            _configSource.Add(configSource);
        }

        public void RegisterCategory(string category, Type configType)
        {
            Categories.Add(category, Activator.CreateInstance(configType));
        }

        private Dictionary<string, object> Categories { get; set; } = new Dictionary<string, object>(StringComparer.InvariantCultureIgnoreCase);
        
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