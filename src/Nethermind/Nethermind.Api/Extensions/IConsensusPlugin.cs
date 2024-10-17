// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;

namespace Nethermind.Api.Extensions
{
    public interface IConsensusPlugin : INethermindPlugin, IBlockProducerFactory
    {
        string SealEngineType { get; }

        IBlockProducerRunner CreateBlockProducerRunner();
    }
}
