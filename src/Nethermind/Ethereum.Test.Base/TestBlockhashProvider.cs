// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;

namespace Ethereum.Test.Base
{
    public class TestBlockhashProvider : IBlockhashProvider
    {
        public Keccak GetBlockhash(BlockHeader currentBlock, in long number)
        {
            if (number != 0)
                return Keccak.Zero;
            return Keccak.Compute(number.ToString());
        }
    }
}
