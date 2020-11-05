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
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Config
{
    public class ConfigProvider : IConfigProvider
    {
        private readonly ConcurrentDictionary<Type, object> _instances = new ConcurrentDictionary<Type, object>();
        
        private readonly List<IConfigSource> _configSource = new List<IConfigSource>();

        public T GetConfig<T>() where T : IConfig
        {
            return (T)GetConfig(typeof(T));
        }

        public object GetConfig(Type configType)
        {
            if (!typeof(IConfig).IsAssignableFrom(configType)) throw new ArgumentException($"Type {configType} is not {typeof(IConfig)}");
                
            if (!_instances.ContainsKey(configType))
            {
                if (!_implementations.ContainsKey(configType))
                {
                    Initialize();
                }

                object config = Activator.CreateInstance(_implementations[configType]);
                _instances[configType] = config!;
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
            
            return _instances[configType];
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
        
        private Dictionary<string, object> Categories { get; set; } = new Dictionary<string, object>(StringComparer.InvariantCultureIgnoreCase);
        
        private readonly Dictionary<Type, Type> _implementations = new Dictionary<Type, Type>();
        
        private readonly TypeDiscovery _typeDiscovery = new TypeDiscovery();
        
        public void Initialize()
        {
            Type type = typeof(IConfig);
            IEnumerable<Type> interfaces = _typeDiscovery.FindNethermindTypes(type).Where(x => x.IsInterface);

            foreach (Type @interface in interfaces)
            {
                Type directImplementation = @interface.GetDirectInterfaceImplementation();

                if (directImplementation != null)
                {
                    Categories.Add(@interface.Name.Substring(1), Activator.CreateInstance(directImplementation));
                    _implementations[@interface] = directImplementation;
                }
            }
        }
    }
}
