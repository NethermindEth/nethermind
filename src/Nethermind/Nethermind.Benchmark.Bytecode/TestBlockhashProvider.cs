// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;

namespace Nethermind.Benchmark.Bytecode
{
    public class TestBlockhashProvider : IBlockhashProvider
    {
        public Keccak GetBlockhash(BlockHeader currentBlock, in long number)
        {
            return Keccak.Compute(number.ToString());
        }
    }
}
