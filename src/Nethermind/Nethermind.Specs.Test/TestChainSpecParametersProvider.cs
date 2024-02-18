// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Specs.Test;

public class TestChainSpecParametersProvider : IChainSpecParametersProvider
{
    public string SealEngineType { get; set; } = Core.SealEngineType.None;
    public ICollection<IChainSpecEngineParameters> AllChainSpecParameters => _parameters.Values;
    public T GetChainSpecParameters<T>() where T : IChainSpecEngineParameters
    {
        return (T)_parameters[typeof(T)];
    }

    public void AddChainSpecParametersProvider<T>(T chainSpecParameters) where T : IChainSpecEngineParameters
    {
        _parameters[typeof(T)] = chainSpecParameters;
    }

    private Dictionary<Type, IChainSpecEngineParameters> _parameters = new();
}
