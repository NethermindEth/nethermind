// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Headers;

public interface IBlockAccessListStore
{
    void Insert(Hash256 blockHash, byte[] bal);
    void Insert(Hash256 blockHash, BlockAccessList bal);
    // void BulkInsert(IReadOnlyList<BlockHeader> headers);
    byte[]? GetRlp(Hash256 blockHash);
    BlockAccessList? Get(Hash256 blockHash);
    // void Cache(BlockHeader header);
    void Delete(Hash256 blockHash);
    // void InsertBlockNumber(Hash256 blockHash, long blockNumber);
    // long? GetBlockNumber(Hash256 blockHash);
}
