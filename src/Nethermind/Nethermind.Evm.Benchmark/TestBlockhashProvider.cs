// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm.Benchmark
{
    public class TestBlockhashProvider : IBlockhashProvider
    {
        public Keccak GetBlockhash(BlockHeader currentBlock, in long number)
        {
            return new Keccak(Keccak.Compute(number.ToString()));
        }
    }
}
