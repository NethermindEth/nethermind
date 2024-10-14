// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System.Reflection;
using System.Text.Json;
using Nethermind.Config;
using Nethermind.Serialization.Json;

namespace Nethermind.Specs.ChainSpecStyle;

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;

public class ChainSpecParametersProvider : IChainSpecParametersProvider
{
    // TODO: test that all IChainSpecEngineParameters have this suffix
    private const string EngineParamsSuffix = "ChainSpecEngineParameters";

    private readonly Dictionary<string, JsonElement> _chainSpecParameters =
        new(StringComparer.InvariantCultureIgnoreCase);

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

            var deserialized = _jsonSerializer.Deserialize(_chainSpecParameters[engineName].ToString(), @class);

            _instances[@class] = (IChainSpecEngineParameters)deserialized;
        }
    }

    public IEnumerable<IChainSpecEngineParameters> AllChainSpecParameters => _instances.Values;

    public T GetChainSpecParameters<T>() where T : IChainSpecEngineParameters
    {
        return (T)_instances[typeof(T)];
    }
}
