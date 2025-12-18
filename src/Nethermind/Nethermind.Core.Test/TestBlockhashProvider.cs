// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;

namespace Nethermind.Core.Test
{
    public class TestBlockhashProvider(ISpecProvider specProvider) : IBlockhashProvider
    {
        public Hash256 GetBlockhash(BlockHeader currentBlock, long number)
            => GetBlockhash(currentBlock, number, specProvider.GetSpec(currentBlock));

        public Hash256 GetBlockhash(BlockHeader currentBlock, long number, IReleaseSpec spec)
        {
            return Keccak.Compute(spec.IsBlockHashInStateAvailable
                ? (Eip2935Constants.RingBufferSize + number).ToString()
                : number.ToString());
        }

        public Task Prefetch(BlockHeader currentBlock, CancellationToken token) => Task.CompletedTask;
    }
}
