// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.State;

namespace Nethermind.Evm.Benchmark
{
    public class TestBlockhashProvider : IBlockhashProvider
    {
        private readonly ISpecProvider _specProvider;
        public TestBlockhashProvider(ISpecProvider specProvider)
        {
            _specProvider = specProvider;
        }

        public Hash256 GetBlockhash(BlockHeader currentBlock, in long number)
        {
            IReleaseSpec spec = _specProvider.GetSpec(currentBlock);
            return Keccak.Compute(spec.IsBlockHashInStateAvailable
                ? (Eip2935Constants.RingBufferSize + number).ToString()
                : (number).ToString());
        }
    }
}
