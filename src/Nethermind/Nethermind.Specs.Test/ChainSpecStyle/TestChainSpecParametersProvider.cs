// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FastEnumUtility;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Specs.Test.ChainSpecStyle;

public class TestChainSpecParametersProvider : IChainSpecParametersProvider
{
    public static readonly TestChainSpecParametersProvider Instance = new();

    public string SealEngineType => TestSealEngineType.NethDev;

    public IEnumerable<IChainSpecEngineParameters> AllChainSpecParameters =>
        new[] { new NethDevChainSpecEngineParameters() };
    public T GetChainSpecParameters<T>() where T : IChainSpecEngineParameters
    {
        if (typeof(T) == typeof(NethDevChainSpecEngineParameters))
        {
            return (T)(object)(new NethDevChainSpecEngineParameters());
        }
        else
        {
            throw new NotSupportedException($"Only NethDev engine in {nameof(TestChainSpecParametersProvider)}");
        }
    }
}
