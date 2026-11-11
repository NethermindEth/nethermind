// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using DotNetty.Buffers;
using DotNetty.Common;
using Nethermind.Core.Buffers;

namespace Nethermind.Network.Rlpx;

/// <summary>
/// A <see cref="ZeroPacket"/> whose content is the readable bytes of a DotNetty buffer, which it holds a reference to.
/// </summary>
/// <remarks>
/// Releasing the packet releases the buffer, so the DotNetty pipeline releases it as it does any reference-counted message.
/// </remarks>
public sealed class NettyBufferPacket(IByteBuffer buffer) : ZeroPacket, IReferenceCounted
{
    public IByteBuffer Buffer { get; } = buffer;

    // Read on each access: the frame merger and the Snappy decoder write into the buffer after creating the packet,
    // which can move it to a larger array.
    public override Memory<byte> Content => Buffer.Array.AsMemory(Buffer.ArrayOffset + Buffer.ReaderIndex, Buffer.ReadableBytes);

    public override IMemoryOwner<byte> LeaseContent() => new NettyBufferMemoryOwner(Buffer);

    public int ReferenceCount => Buffer.ReferenceCount;

    public IReferenceCounted Retain()
    {
        Buffer.Retain();
        return this;
    }

    public IReferenceCounted Retain(int increment)
    {
        Buffer.Retain(increment);
        return this;
    }

    public IReferenceCounted Touch()
    {
        Buffer.Touch();
        return this;
    }

    public IReferenceCounted Touch(object hint)
    {
        Buffer.Touch(hint);
        return this;
    }

    public bool Release() => Buffer.Release();

    public bool Release(int decrement) => Buffer.Release(decrement);
}
