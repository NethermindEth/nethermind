// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm.Benchmark
{
    public class TestBlockhashProvider : IBlockhashProvider
    {
        public Keccak GetBlockhash(BlockHeader currentBlock, in long number)
        {
            return Keccak.Compute(number.ToString());
        }
    }
}
