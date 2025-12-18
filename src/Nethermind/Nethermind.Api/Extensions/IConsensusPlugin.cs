// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus;

namespace Nethermind.Api.Extensions
{
    public interface IConsensusPlugin : INethermindPlugin, IBlockProducerFactory, IBlockProducerRunnerFactory
    {
        Type ApiType => typeof(NethermindApi);
    }
}
