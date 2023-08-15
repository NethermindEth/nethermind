// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Core;

/// <summary>
/// A BlockBody[] that must be explicitly disposed or there will be memory leak. May uses netty's buffer directly.
/// I don't like the name too. Any idea?
/// </summary>
public class UnmanagedBlockBodies : IDisposable
{
    private BlockBody?[]? _rawBodies = null;

    private IMemoryOwner<byte>? _memoryOwner = null;

    public UnmanagedBlockBodies(BlockBody?[] bodies, IMemoryOwner<byte>? memoryOwner = null)
    {
        _rawBodies = bodies;
    }

    public BlockBody?[] Bodies => _rawBodies;

    public void Dispose()
    {
        _memoryOwner?.Dispose();
    }
}
