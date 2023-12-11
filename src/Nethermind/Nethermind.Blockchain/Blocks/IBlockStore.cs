// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Blocks;

/// <summary>
/// Raw block store. Does not know or care about blockchain or blocktree, only encoding/decoding to kv store.
/// Generally you probably need IBlockTree instead of this.
/// </summary>
public interface IBlockStore
{
    void Insert(Block block, WriteFlags writeFlags = WriteFlags.None);
    void Delete(long blockNumber, Hash256 blockHash);
    Block? Get(long blockNumber, Hash256 blockHash, bool shouldCache = true);
    IEnumerable<Block> GetAll();
    ReceiptRecoveryBlock? GetReceiptRecoveryBlock(long blockNumber, Hash256 blockHash);
    void Cache(Block block);


    // These two are used by blocktree. Try not to use them...
    void SetMetadata(byte[] key, byte[] value);
    byte[]? GetMetadata(byte[] key);
}
