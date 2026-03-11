// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Headers;

public interface IBlockAccessListStore
{
    void InsertFromBlock(Block block)
    {
        if (block.EncodedBlockAccessList is not null)
        {
            Insert(block.Hash, block.EncodedBlockAccessList);
        }
        else if (block.BlockAccessList is not null)
        {
            Insert(block.Hash, block.BlockAccessList);
        }
    }

    void Insert(Hash256 blockHash, byte[] bal);
    void Insert(Hash256 blockHash, BlockAccessList bal);
    byte[]? GetRlp(Hash256 blockHash);
    BlockAccessList? Get(Hash256 blockHash);
    void Delete(Hash256 blockHash);
}
