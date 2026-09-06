// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;

namespace Ethereum.Test.Base
{
    public class TestBlockhashProvider : IBlockhashProvider
    {
        public Hash256? GetBlockhash(BlockHeader currentBlock, ulong number, IReleaseSpec? spec)
        {
            if (number >= currentBlock.Number)
            {
                return null;
            }

            ulong depth = currentBlock.Number - number;
            if (depth > BlockhashProvider.MaxDepth)
            {
                return null;
            }

            return number != 0 ? Keccak.Zero : Keccak.Compute(number.ToString());
        }

        public Task Prefetch(BlockHeader currentBlock, CancellationToken token) => Task.CompletedTask;
    }
}
