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

        public Hash256 GetBlockhash(BlockHeader currentBlock, in long number)
        {
            return Keccak.Compute(number.ToString());
        }
    }
}
