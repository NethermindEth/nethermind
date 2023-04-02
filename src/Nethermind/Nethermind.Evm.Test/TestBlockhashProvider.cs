// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm.Test
{
    public class TestBlockhashProvider : IBlockhashProvider
    {
        public static TestBlockhashProvider Instance = new();

        private TestBlockhashProvider()
        {
        }

        public Keccak GetBlockhash(BlockHeader currentBlock, in long number)
        {
            return new Keccak(Keccak.Compute(number.ToString()));
        }
    }
}
