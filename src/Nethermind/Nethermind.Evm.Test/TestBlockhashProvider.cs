// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Test
{
    public class TestBlockhashProvider : IBlockhashProvider
    {
        private readonly ISpecProvider _specProvider;
        public TestBlockhashProvider(ISpecProvider specProvider)
        {
            _specProvider = specProvider;
        }
        public Hash256 GetBlockhash(BlockHeader currentBlock, long number)
            => GetBlockhash(currentBlock, number, _specProvider.GetSpec(currentBlock));

        public Hash256 GetBlockhash(BlockHeader currentBlock, long number, IReleaseSpec? spec)
        {
            return Keccak.Compute(spec.IsBlockHashInStateAvailable
                ? (Eip2935Constants.RingBufferSize + number).ToString()
                : (number).ToString());
        }
    }
}
