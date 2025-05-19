// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System.Text.Json;
using Nethermind.Serialization.Json;

namespace Nethermind.Specs.ChainSpecStyle;

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;

public class ChainSpecParametersProvider : IChainSpecParametersProvider
{
    private readonly Dictionary<string, JsonElement> _chainSpecParameters;
    private readonly Dictionary<Type, IChainSpecEngineParameters> _instances = new();
    private readonly IJsonSerializer _jsonSerializer;

    public string SealEngineType { get; }

    public ChainSpecParametersProvider(Dictionary<string, JsonElement> engineParameters, IJsonSerializer jsonSerializer)
    {
        _chainSpecParameters = new Dictionary<string, JsonElement>(engineParameters, StringComparer.InvariantCultureIgnoreCase);
        _jsonSerializer = jsonSerializer;

        InitializeInstances();
        SealEngineType = CalculateSealEngineType();
    }

    private string CalculateSealEngineType()
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

        return result ?? throw new InvalidOperationException("No seal engine in chain spec");
    }

    private void InitializeInstances()
    {
        IEnumerable<Type> types = TypeDiscovery.FindNethermindBasedTypes(typeof(IChainSpecEngineParameters)).Where(static x => x.IsClass);
        foreach (Type type in types)
        {
            IChainSpecEngineParameters instance = (IChainSpecEngineParameters)Activator.CreateInstance(type)!;
            if (_chainSpecParameters.TryGetValue(instance.EngineName!, out JsonElement json))
            {
                _instances[type] = (IChainSpecEngineParameters)_jsonSerializer.Deserialize(json.ToString(), type);
            }
        }
    }

    public IEnumerable<IChainSpecEngineParameters> AllChainSpecParameters => _instances.Values;

    public T GetChainSpecParameters<T>() where T : IChainSpecEngineParameters => (T)_instances[typeof(T)];
}
