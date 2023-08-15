// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;

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

    ~UnmanagedBlockBodies() {
        _memoryOwner?.Dispose();
    }

    public BlockBody?[] Bodies => _rawBodies;

    public void Dispose()
    {
        _memoryOwner?.Dispose();
        _memoryOwner = null;
    }
}
