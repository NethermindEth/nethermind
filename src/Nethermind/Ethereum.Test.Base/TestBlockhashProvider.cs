// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;

namespace Ethereum.Test.Base
{
    public class TestBlockhashProvider : IBlockhashProvider
    {
        private readonly Dictionary<long, Hash256> _blockHashes = new();
        public Hash256 GetBlockhash(BlockHeader currentBlock, in long number)
        {
            return _blockHashes[number] ?? throw new InvalidDataException($"BlockHash for block {number} not provided");
        }

        public void Insert(Hash256 blockHash, long number)
        {
            _blockHashes.Add(number, blockHash);
        }
    }
}
