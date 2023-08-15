// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using DotNetty.Buffers;

namespace Nethermind.Core.Buffers;

/// <summary>
/// A wrapper around IByteBuffer to expose standard Memory.
/// Internally, IByteBuffer is already a standard array, so no need for a MemoryManager.
/// </summary>
public class NettyBufferMemoryOwner : IMemoryOwner<byte>
{
    private readonly IByteBuffer _byteBuffer;
    private bool _disposed = false;

    public NettyBufferMemoryOwner(IByteBuffer byteBuffer)
    {
        _byteBuffer = byteBuffer;
        _byteBuffer.Retain();
    }

    ~NettyBufferMemoryOwner() {
        Dispose(false);
    }

    public void Dispose()
    {
        Dispose(false);
    }

    protected virtual void Dispose(bool isDisposing)
    {
        if (!_disposed)
        {
            _byteBuffer.Release();
            _disposed = true;
        }
    }

    public Memory<byte> Memory => _byteBuffer
        .Array.AsMemory()
        .Slice(_byteBuffer.ArrayOffset + _byteBuffer.ReaderIndex, _byteBuffer.ReadableBytes);
}
