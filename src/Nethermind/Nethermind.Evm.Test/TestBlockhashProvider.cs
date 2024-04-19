// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.State;

namespace Nethermind.Evm.Test
{
    public class TestBlockhashProvider : IBlockhashProvider
    {
        public static TestBlockhashProvider Instance = new();

        private TestBlockhashProvider()
        {
        }

        public Hash256 GetBlockhash(BlockHeader currentBlock, in long number, IReleaseSpec spec, IWorldState stateProvider)
        {
            if (spec.IsBlockHashInStateAvailable)
                return Keccak.Compute((Eip2935Constants.RingBufferSize + number).ToString());
            return Keccak.Compute((number).ToString());
        }
    }
}
