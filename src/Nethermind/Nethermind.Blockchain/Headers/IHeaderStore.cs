// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Headers;

public interface IHeaderStore
{
    void Insert(BlockHeader header);
    void BulkInsert(IReadOnlyList<BlockHeader> headers);
    BlockHeader? Get(Hash256 blockHash, long? blockNumber = null);
    bool Cache(BlockHeader header, bool hasDifficulty, bool isCanonical = false);
    bool TryGetCache(Hash256 blockHash, bool needsDifficulty, bool requiresCanonical, [NotNullWhen(true)] out BlockHeader? header);
    void DeleteCanonicalCache(Hash256 blockHash);
    void Delete(Hash256 blockHash);
    void InsertBlockNumber(Hash256 blockHash, long blockNumber);
    long? GetBlockNumber(Hash256 blockHash);
}
