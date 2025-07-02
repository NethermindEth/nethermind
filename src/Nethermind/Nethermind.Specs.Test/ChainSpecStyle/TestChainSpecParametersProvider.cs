// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Ethash;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Specs.Test.ChainSpecStyle;

public class TestChainSpecParametersProvider : IChainSpecParametersProvider
{
    public static readonly TestChainSpecParametersProvider NethDev = new(new NethDevChainSpecEngineParameters());

    private readonly IChainSpecEngineParameters _parameters;

    public TestChainSpecParametersProvider(IChainSpecEngineParameters parameters)
    {
        _parameters = parameters;
    }

    public string SealEngineType => _parameters.SealEngineType!;

    public IEnumerable<IChainSpecEngineParameters> AllChainSpecParameters =>
        new[] { _parameters };
    public T GetChainSpecParameters<T>() where T : IChainSpecEngineParameters
    {
        if (typeof(T) == _parameters.GetType())
        {
            return (T)_parameters;
        }
        else
        {
            throw new NotSupportedException($"Only {_parameters.GetType().Name} engine in {nameof(TestChainSpecParametersProvider)}");
        }
    }
}
