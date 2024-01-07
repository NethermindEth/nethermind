// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Serialization.Json;

namespace Nethermind.Specs.ChainSpecStyle;

public class ChainSpecParametersProvider : IChainSpecParametersProvider
{
    // TODO: test that all IChainSpecEngineParameters have this suffix
    private const string EngineParamsSuffix = "ChainSpecEngineParameters";
    private readonly Dictionary<string, object> _chainSpecParameters =
        new(StringComparer.InvariantCultureIgnoreCase);
    private readonly ConcurrentDictionary<Type, IChainSpecEngineParameters> _instances = new();
    private readonly EthereumJsonSerializer _serializer;
    public string SealEngineType { get; }

    public ChainSpecParametersProvider(ChainSpec chainSpec, IJsonSerializer serializerr)
    {
        _serializer = (EthereumJsonSerializer)serializerr;
        foreach (var parameters in chainSpec.AdditionalParameters)
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
            try
            {
                var value = (IChainSpecEngineParameters)_serializer.Deserialize(_chainSpecParameters[engineName].ToString(), @class);
                _instances[@class] = value;
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"Cannot parse of {engineName} engine from chainSpec", e);
            }
        }
    }

    public ICollection<IChainSpecEngineParameters> AllChainSpecParameters => _instances.Values;

    public T GetChainSpecParameters<T>() where T : IChainSpecEngineParameters
    {
        return (T)_instances[typeof(T)];
    }
}
