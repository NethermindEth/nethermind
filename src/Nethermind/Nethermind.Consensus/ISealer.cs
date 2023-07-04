// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus
{
    public interface ISealer
    {
        Task<Block> SealBlock(Block block, CancellationToken cancellationToken);

        bool CanSeal(long blockNumber, Keccak parentHash);

        Address Address { get; }
    }
}
