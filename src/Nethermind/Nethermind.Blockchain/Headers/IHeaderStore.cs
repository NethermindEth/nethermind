// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Headers;

public interface IHeaderStore
{
    void Insert(BlockHeader header);
    BlockHeader? Get(Hash256 blockHash, bool shouldCache, long? blockNumber = null);
    void Cache(BlockHeader header);
    void Delete(Hash256 blockHash);
    void InsertBlockNumber(Hash256 blockHash, long blockNumber);
    long? GetBlockNumber(Hash256 blockHash);
}
