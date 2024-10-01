// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System.Reflection;
using System.Text.Json;
using Nethermind.Config;

namespace Nethermind.Specs.ChainSpecStyle;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;

public class ChainSpecParametersProvider : IChainSpecParametersProvider
{
    // TODO: test that all IChainSpecEngineParameters have this suffix
    private const string EngineParamsSuffix = "ChainSpecEngineParameters";

    private readonly Dictionary<string, JsonElement> _chainSpecParameters =
        new(StringComparer.InvariantCultureIgnoreCase);

    private readonly ConcurrentDictionary<Type, IChainSpecEngineParameters> _instances = new();
    public string SealEngineType { get; }

    public ChainSpecParametersProvider(Dictionary<string, JsonElement> engineParameters)
    {
        foreach (var parameters in engineParameters)
        {
            _chainSpecParameters[parameters.Key] = parameters.Value;
        }

        InitializeInstances();
        SealEngineType = CalculateSealEngineType();
    }

    string CalculateSealEngineType()
    {
        string? result = null;
        foreach (IChainSpecEngineParameters item in _instances.Values)
        {
            if (item.SealEngineType is not null)
            {
                if (result is not null)
                {
                    throw new InvalidOperationException("Multiple seal engines in chain spec");
                }

                result = item.SealEngineType;
            }
        }

        if (result is null)
        {
            throw new InvalidOperationException("No seal engine in chain spec");
        }

        return result;
    }

    private void InitializeInstances()
    {
        Type type = typeof(IChainSpecEngineParameters);
        IEnumerable<Type> types = TypeDiscovery.FindNethermindBasedTypes(type).Where(x => x.IsClass);
        foreach (Type @class in types)
        {
            string engineName = @class.Name.Remove(@class.Name.Length - EngineParamsSuffix.Length);
            if (!_chainSpecParameters.ContainsKey(engineName)) continue;

            var instance = (IChainSpecEngineParameters)@class.GetConstructor(Type.EmptyTypes)!.Invoke(Array.Empty<object>());

            foreach (PropertyInfo property in @class.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.Name == "SealEngineType") continue;
                JsonProperty? jsonProperty = _chainSpecParameters[engineName].EnumerateObject().FirstOrDefault(p =>
                    string.Compare(p.Name, property.Name, StringComparison.InvariantCultureIgnoreCase) == 0);

                if (jsonProperty is null)
                {
                    throw new FormatException($"ChainSpec property {property.Name} is not set");
                }

                object value = ConfigSourceHelper.ParseValue(property.PropertyType, jsonProperty.Value.Value.ToString(), "chainspec", property.Name);
                property.SetValue(instance, value);
            }

            _instances[@class] = instance;
        }
    }

    public ICollection<IChainSpecEngineParameters> AllChainSpecParameters => _instances.Values;

    public T GetChainSpecParameters<T>() where T : IChainSpecEngineParameters
    {
        return (T)_instances[typeof(T)];
    }
}
