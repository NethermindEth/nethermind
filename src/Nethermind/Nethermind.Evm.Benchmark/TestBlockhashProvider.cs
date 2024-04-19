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
        public Hash256 GetBlockhash(BlockHeader currentBlock, in long number, IReleaseSpec spec, IWorldState stateProvider)
        {
            return Keccak.Compute(number.ToString());
        }
    }
}
