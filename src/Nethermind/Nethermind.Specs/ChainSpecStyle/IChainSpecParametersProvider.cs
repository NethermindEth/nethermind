// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Specs.ChainSpecStyle;

public interface IChainSpecParametersProvider
{
    string SealEngineType { get; }

    ICollection<IChainSpecEngineParameters> AllChainSpecParameters { get; }

    T GetChainSpecParameters<T>() where T : IChainSpecEngineParameters;
}
