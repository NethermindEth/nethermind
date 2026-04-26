// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.ChainSpecStyle;

using System.Collections.Generic;

public interface IChainSpecParametersProvider
{
    string SealEngineType { get; }
    IEnumerable<IChainSpecEngineParameters> AllChainSpecParameters { get; }
    T GetChainSpecParameters<T>() where T : IChainSpecEngineParameters;
}
