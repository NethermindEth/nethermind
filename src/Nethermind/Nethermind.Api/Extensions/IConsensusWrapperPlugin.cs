// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus;

namespace Nethermind.Api.Extensions
{
    public interface IConsensusWrapperPlugin : INethermindPlugin
    {
        Task<IBlockProducer> InitBlockProducer(IConsensusPlugin consensusPlugin);
        bool Enabled { get; }
    }
}
