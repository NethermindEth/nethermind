// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Threading;

namespace Nethermind.Network.Rlpx.Sockets;

/// <summary>
/// A received packet over a rented array, which goes back to its pool when the last reference is released.
/// </summary>
/// <remarks>
/// The transport holds one reference and releases it once the session has handled the packet. Each
/// <see cref="LeaseContent"/> adds a reference, so a serializer that keeps the content needs no copy.
/// </remarks>
/// <param name="pool">The pool the buffer was rented from.</param>
/// <param name="buffer">A rented array, which the packet now owns.</param>
/// <param name="offset">Where the content starts.</param>
/// <param name="length">The length of the content.</param>
internal sealed class PooledPacket(ArrayPool<byte> pool, byte[] buffer, int offset, int length) : ZeroPacket
{
    private byte[] _buffer = buffer;
    private int _offset = offset;
    private int _length = length;
    private int _references = 1;

    public override Memory<byte> Content => _buffer.AsMemory(_offset, _length);

    /// <summary>Gets the rented array, for writing the content while the transport holds the only reference.</summary>
    public byte[] Buffer => _buffer;

    /// <summary>Gets whether the transport holds the only reference, so it can reuse the buffer.</summary>
    public bool IsExclusive => Volatile.Read(ref _references) == 1;

    public override IMemoryOwner<byte> LeaseContent()
    {
        Interlocked.Increment(ref _references);
        return new Lease(this);
    }

    /// <summary>Sets the content to a range of the buffer, while the transport holds the only reference.</summary>
    public void SetContent(int offset, int length)
    {
        Debug.Assert(IsExclusive, "Only an unshared packet can be rewritten.");
        _offset = offset;
        _length = length;
    }

    /// <summary>Replaces the buffer with a larger rented one that keeps the content, while the transport holds the only reference.</summary>
    public void Grow(int capacity)
    {
        Debug.Assert(IsExclusive, "Only an unshared packet can be rewritten.");
        byte[] buffer = pool.Rent(capacity);
        _buffer.AsSpan(_offset, _length).CopyTo(buffer);
        pool.Return(_buffer);
        _buffer = buffer;
        _offset = 0;
    }

    /// <summary>Releases a reference, returning the buffer to the pool when it was the last one.</summary>
    public void Release()
    {
        int references = Interlocked.Decrement(ref _references);
        Debug.Assert(references >= 0, "A packet was released more times than it was referenced.");
        if (references == 0)
        {
            pool.Return(_buffer);
            _buffer = [];
        }
    }

    private sealed class Lease(PooledPacket packet) : IMemoryOwner<byte>
    {
        private PooledPacket? _packet = packet;
        private readonly Memory<byte> _memory = packet.Content;

        public Memory<byte> Memory => _packet is null ? throw new ObjectDisposedException(nameof(Lease)) : _memory;

        public void Dispose() => Interlocked.Exchange(ref _packet, null)?.Release();
    }
}
