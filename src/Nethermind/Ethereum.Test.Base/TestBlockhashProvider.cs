// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;

namespace Ethereum.Test.Base
{
    public class TestBlockHashProvider : IBlockHashProvider
    {
        public Hash256 GetBlockHash(BlockHeader currentBlock, in long number)
        {
            if (number != 0)
                return Keccak.Zero;
            return Keccak.Compute(number.ToString());
        }
    }
}
