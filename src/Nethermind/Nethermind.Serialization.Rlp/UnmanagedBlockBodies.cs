// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Core;

/// <summary>
/// A BlockBody[] that must be explicitly disposed or there will be memory leak. May uses netty's buffer directly.
/// I don't like the name too. Any idea?
/// </summary>
public class UnmanagedBlockBodies : IDisposable
{
    private BlockBody?[] _rawBodies = null;

    private IMemoryOwner<byte>? _memoryOwner = null;

    public UnmanagedBlockBodies(BlockBody?[] bodies, IMemoryOwner<byte>? memoryOwner = null)
    {
        _rawBodies = bodies;
        _memoryOwner = memoryOwner;
    }

    public BlockBody?[] Bodies => _rawBodies;

    public void DisOwn()
    {
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
