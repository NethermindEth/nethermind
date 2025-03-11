// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using NonBlocking;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Config;

public class ConfigProvider : IConfigProvider
{

    private readonly ConcurrentDictionary<Type, IConfig> _instances = new();

    private readonly List<IConfigSource> _configSource = [];
    private Dictionary<string, object> Categories { get; set; } = new(StringComparer.InvariantCultureIgnoreCase);

    private readonly Dictionary<Type, Type> _implementations = [];

    public ConfigProvider()
    {
    }

    public ConfigProvider(params IConfig[] existingConfigs)
    {
        foreach (IConfig existingConfig in existingConfigs)
        {
            Type type = existingConfig.GetType();
            if (!type.IsInterface)
            {
                // Try to get the interface type of the config
                foreach (Type @interface in type.GetInterfaces())
                {
                    if (@interface.Name == $"I{type.Name}")
                    {
                        type = @interface;
                    }
                }
            }
            _instances[type] = existingConfig;
        }
    }

    private static IList<(Type ConfigType, Type DirectImplementation)> _configTypesCache = null!;
    private static IList<(Type ConfigType, Type DirectImplementation)> ConfigTypesFromAssembly
    {
        get
        {
            if (_configTypesCache is not null) return _configTypesCache;

            Type type = typeof(IConfig);
            IEnumerable<Type> interfaces = TypeDiscovery.FindNethermindBasedTypes(type).Where(static x => x.IsInterface);
            IList<(Type ConfigType, Type DirectImplementation)> types =
                new List<(Type ConfigType, Type DirectImplementation)>();

            foreach (Type @interface in interfaces)
            {
                Type directImplementation = @interface.GetDirectInterfaceImplementation();

                if (directImplementation is not null)
                {
                    types.Add((@interface, directImplementation));
                }
            }

            _configTypesCache = types;
            return types;
        }
    }

    public T GetConfig<T>() where T : IConfig
    {
        return (T)GetConfig(typeof(T));
    }

    public IConfig GetConfig(Type configType)
    {
        if (!typeof(IConfig).IsAssignableFrom(configType)) throw new ArgumentException($"Type {configType} is not {typeof(IConfig)}");

        if (!_instances.ContainsKey(configType))
        {
            if (!_implementations.ContainsKey(configType))
            {
                Initialize();
            }
        }

        return _instances[configType];
    }

    public object GetRawValue(string category, string name)
    {
        for (int i = 0; i < _configSource.Count; i++)
        {
            (bool isSet, string str) = _configSource[i].GetRawValue(category, name);
            if (isSet)
            {
                return str;
            }
        }

        return Categories.TryGetValue(category, out object value) ? value.GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .SingleOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            ?.GetValue(value) : null;
    }

    public void AddSource(IConfigSource configSource)
    {
        _configSource.Add(configSource);
    }

    public void Initialize()
    {
        foreach ((Type @interface, Type directImplementation) in ConfigTypesFromAssembly)
        {
            if (_instances.ContainsKey(@interface)) continue;

            if (directImplementation is not null)
            {
                Categories.Add(@interface.Name[1..],
                    Activator.CreateInstance(directImplementation));

                _implementations[@interface] = directImplementation;

                object config = Activator.CreateInstance(_implementations[@interface]);
                _instances[@interface] = (IConfig)config!;

                foreach (PropertyInfo propertyInfo in config.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    for (int i = 0; i < _configSource.Count; i++)
                    {
                        string category = @interface.IsAssignableFrom(typeof(INoCategoryConfig)) ? null : config.GetType().Name;
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
        }
    }

    public (string ErrorMsg, IList<(IConfigSource Source, string Category, string Name)> Errors) FindIncorrectSettings()
    {
        if (_instances.IsEmpty)
        {
            Initialize();
        }

        var propertySet = _instances.Values
            .SelectMany(i => i.GetType()
                .GetProperties()
                .Select(p => GetKey(i.GetType().Name, p.Name)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<(IConfigSource Source, string Category, string Name)> incorrectSettings = [];

        // Skip the validation for ArgsConfigSource items as they are already validated by the CLI parser
        foreach (IConfigSource source in _configSource.Where(s => s is not ArgsConfigSource))
        {
            var configs = source.GetConfigKeys();

            foreach ((string category, string name) in configs)
            {
                if (!propertySet.Contains(GetKey(category, name)))
                {
                    incorrectSettings.Add((source, category, name));
                }
            }
        }

        var msg = string.Join(Environment.NewLine, incorrectSettings.Select(s => $"ConfigType:{GetConfigSourceName(s.Source)}|Category:{s.Category}|Name:{s.Name}"));

        return (msg, incorrectSettings);

        static string GetConfigSourceName(IConfigSource source) => source switch
        {
            ArgsConfigSource => "RuntimeOption",
            EnvConfigSource => "EnvironmentVariable(NETHERMIND_*)",
            JsonConfigSource => "JsonConfigFile",
            _ => source.ToString()
        };

        static string GetKey(string category, string name)
        {
            if (string.IsNullOrEmpty(category))
            {
                category = nameof(NoCategoryConfig);
            }
            else if (!category.EndsWith("config", StringComparison.OrdinalIgnoreCase))
            {
                category = $"{category}Config";
            }

            return $"{category}.{name}";
        }
    }
}
