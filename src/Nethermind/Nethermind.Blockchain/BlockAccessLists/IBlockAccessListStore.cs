// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.BlockAccessLists;

/// <summary>Stores block access lists by block number and hash.</summary>
/// <remarks>
/// Sync holds <see cref="WriteLock"/> around its pruning-cutoff check and insert. Implementations hold the same lock
/// while deleting a range, releasing it before storage reclaim. Decorators must forward the underlying store's lock.
/// </remarks>
public interface IBlockAccessListStore
{
    /// <summary>The shared lock serializing sync insertion with range deletion.</summary>
    Lock WriteLock { get; }

    void InsertFromBlock(Block block)
    {
        Hash256 blockHash = block.Hash ?? ThrowMissingBlockHash(nameof(block));

        if (block.EncodedBlockAccessList is not null)
        {
            Insert(block.Number, blockHash, block.EncodedBlockAccessList);
        }
        else if (block.BlockAccessList is not null && !Exists(block.Number, blockHash))
        {
            Insert(block.Number, blockHash, block.BlockAccessList);
        }

        // Release BAL data after persistence to prevent memory accumulation in block caches.
        // The data can be re-read from the store if needed.
        block.GeneratedBlockAccessList = null;
        block.EncodedBlockAccessList = null;
    }

    /// <summary>Defers the block-access-list write off the block-suggestion path, freeing the live BAL
    /// immediately; falls back to <see cref="InsertFromBlock"/> when deferral is disabled. See IStatePersistenceBarrier.</summary>
    void InsertFromBlockDeferred(Block block) => InsertFromBlock(block);

    void Insert(ulong blockNumber, Hash256 blockHash, byte[] bal);
    void Insert(ulong blockNumber, Hash256 blockHash, scoped ReadOnlySpan<byte> bal);
    void Insert(ulong blockNumber, Hash256 blockHash, ReadOnlyBlockAccessList bal);
    MemoryManager<byte>? GetRlp(ulong blockNumber, Hash256 blockHash);
    ReadOnlyBlockAccessList? Get(ulong blockNumber, Hash256 blockHash);
    bool Exists(ulong blockNumber, Hash256 blockHash);
    void Delete(ulong blockNumber, Hash256 blockHash);

    /// <summary>Drops every access list in <c>[fromInclusive, toExclusive)</c> in one operation.</summary>
    void DeleteRange(ulong fromInclusive, ulong toExclusive);

    [DoesNotReturn, StackTraceHidden]
    private static Hash256 ThrowMissingBlockHash(string paramName) =>
        throw new ArgumentException("Block hash is required to persist a block access list.", paramName);
}
