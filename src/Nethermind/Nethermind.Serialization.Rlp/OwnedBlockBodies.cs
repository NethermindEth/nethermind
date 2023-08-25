// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Core;

/// <summary>
/// A holder for BlockBody[] that must be explicitly disposed or there will be memory leak. May uses netty's buffer directly.
/// BlockBody may contain `Memory<byte>` that is explicitly managed. Reusing `BlockBody` from this object after Dispose
/// is likely to cause corrupted `BlockBody`.
/// </summary>
public class OwnedBlockBodies : IDisposable
{
    private BlockBody?[]? _rawBodies = null;

    private IMemoryOwner<byte>? _memoryOwner = null;

    public OwnedBlockBodies(BlockBody?[]? bodies, IMemoryOwner<byte>? memoryOwner = null)
    {
        _rawBodies = bodies;
        _memoryOwner = memoryOwner;
    }

    public BlockBody?[]? Bodies => _rawBodies;

    /// <summary>
    /// Disown the `BlockBody`, copying any `Memory<byte>` so that it does not depend on the `_memoryOwner.`
    /// </summary>
    public void Disown()
    {
        if (_memoryOwner == null) return;

        foreach (BlockBody? blockBody in Bodies)
        {
            if (blockBody == null) continue;
            foreach (Transaction tx in blockBody.Transactions)
            {
                Keccak? _ = tx.Hash; // Just need to trigger hash calculation
                if (tx.Data != null)
                {
                    tx.Data = tx.Data.Value.ToArray();
                }
            }
        }

        _memoryOwner?.Dispose();
        _memoryOwner = null;
    }

    public void Dispose()
    {
        if (_memoryOwner == null) return;

        foreach (BlockBody? blockBody in Bodies)
        {
            if (blockBody == null) continue;
            foreach (Transaction tx in blockBody.Transactions)
            {
                TxDecoder.TxObjectPool.Return(tx);
            }
        }

        _memoryOwner?.Dispose();
        _memoryOwner = null;
    }
}
