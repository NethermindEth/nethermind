// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.BlockAccessLists;

public interface IBlockAccessListStore
{
    void InsertFromBlock(Block block)
    {
        Hash256 blockHash = block.Hash ?? ThrowMissingBlockHash(nameof(block));

        if (block.EncodedBlockAccessList is not null)
        {
            Insert(block.Number, blockHash, block.EncodedBlockAccessList);
        }
        else if (block.BlockAccessList is not null)
        {
            Insert(block.Number, blockHash, block.BlockAccessList);
        }

        // Release BAL data after persistence to prevent memory accumulation in block caches.
        // The data can be re-read from the store if needed.
        block.GeneratedBlockAccessList = null;
        block.EncodedBlockAccessList = null;
    }

    void Insert(long blockNumber, Hash256 blockHash, byte[] bal);
    void Insert(long blockNumber, Hash256 blockHash, scoped ReadOnlySpan<byte> bal);
    void Insert(long blockNumber, Hash256 blockHash, ReadOnlyBlockAccessList bal);
    MemoryManager<byte>? GetRlp(long blockNumber, Hash256 blockHash);
    ReadOnlyBlockAccessList? Get(long blockNumber, Hash256 blockHash);
    bool Exists(long blockNumber, Hash256 blockHash);
    void Delete(long blockNumber, Hash256 blockHash);

    [DoesNotReturn, StackTraceHidden]
    private static Hash256 ThrowMissingBlockHash(string paramName) =>
        throw new ArgumentException("Block hash is required to persist a block access list.", paramName);
}
