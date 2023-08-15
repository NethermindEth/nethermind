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
public class NettyBufferMemoryOwner: IMemoryOwner<byte>
{
    private readonly IByteBuffer _byteBuffer;

    public NettyBufferMemoryOwner(IByteBuffer byteBuffer)
    {
        _byteBuffer = byteBuffer;
        _byteBuffer.Retain();
    }

    public void Dispose()
    {
        _byteBuffer.Release();
    }

    public Memory<byte> Memory => _byteBuffer
        .Array.AsMemory()
        .Slice(_byteBuffer.ArrayOffset + _byteBuffer.ReaderIndex, _byteBuffer.ReadableBytes);
}
