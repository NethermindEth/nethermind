// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System;
using System.Buffers;
using System.Threading;

namespace Nethermind.Core.Buffers;

/// <summary>
/// Array-pool backed buffer with explicit reference counting for ownership transfer.
/// </summary>
/// <remarks>
/// Ported from dotnet-libp2p (<c>add-channels-ii</c> channel buffers) so the devp2p
/// message layer can rent, share, and release pooled buffers without taking a
/// dependency on the libp2p transport stack. The DotNetty pipeline stays in place
/// for RLPx framing and UDP discovery; only message buffering moves to this type.
/// <para>
/// Ownership is linear: the renter owns the buffer and transfers ownership by passing
/// the buffer or a <see cref="Slice"/> onward exactly once; the final holder disposes it.
/// Copying a <see cref="Slice"/> does not retain — use <c>SliceRange</c> for another
/// owned view — and a <see cref="Span{T}"/>/<see cref="Memory{T}"/> obtained from a
/// buffer or slice must not outlive its disposal, as it is not reference-counted.
/// </para>
/// <para>
/// <see cref="RentSlice"/>, <see cref="Slice.TryPrepend"/> and
/// <see cref="Slice.PrependableLength"/> currently have no production callers; they are
/// the reserved channel-handoff surface (headroom prepends for framing headers) kept
/// from upstream for the next step of the channels work, with coverage in
/// <c>PooledBufferTests</c>.
/// </para>
/// </remarks>
public sealed class PooledBuffer : IMemoryOwner<byte>
{
    private ArrayPool<byte>? _pool;
    private byte[] _buffer = [];
    private int _length;
    private int _refCount;
    private int _returned;
    private int _disposed;

    private PooledBuffer(ArrayPool<byte> pool, byte[] buffer, int length)
    {
        _pool = pool;
        _buffer = buffer;
        _length = length;
        _refCount = 1;
    }

    public static PooledBuffer Rent(int length, ArrayPool<byte>? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        ArrayPool<byte> chosenPool = pool ?? ArrayPool<byte>.Shared;
        return new PooledBuffer(chosenPool, chosenPool.Rent(length), length);
    }

    public static Slice RentSlice(int length, int headroom = 0, int tailroom = 0, ArrayPool<byte>? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfNegative(headroom);
        ArgumentOutOfRangeException.ThrowIfNegative(tailroom);

        int totalLength = checked(length + headroom + tailroom);
        ArrayPool<byte> chosenPool = pool ?? ArrayPool<byte>.Shared;
        PooledBuffer owner = new(chosenPool, chosenPool.Rent(totalLength), totalLength);
        return Slice.TakePublicOwnership(owner, headroom, length, 0);
    }

    public int Length => _length;
    internal int RefCount => Volatile.Read(ref _refCount);
    internal byte[] Array => _buffer;
    public Memory<byte> Memory => new(_buffer, 0, _length);
    public ReadOnlyMemory<byte> ReadOnlyMemory => new(_buffer, 0, _length);
    public Span<byte> Span => new(_buffer, 0, _length);
    public ReadOnlySpan<byte> ReadOnlySpan => new(_buffer, 0, _length);

    public Slice this[Range range]
    {
        get
        {
            (int offset, int length) = range.GetOffsetAndLength(_length);
            return LeaseSlice(offset, length, offset);
        }
    }

    internal void Retain()
    {
        int refs;
        do
        {
            refs = Volatile.Read(ref _refCount);
            if (refs <= 0 || refs == int.MaxValue)
            {
                throw new ObjectDisposedException(nameof(PooledBuffer));
            }
        } while (Interlocked.CompareExchange(ref _refCount, refs + 1, refs) != refs);
    }

    internal void Release()
    {
        int refs = Interlocked.Decrement(ref _refCount);
        if (refs < 0)
        {
            throw new ObjectDisposedException(nameof(PooledBuffer));
        }

        if (refs == 0 && Interlocked.Exchange(ref _returned, 1) == 0)
        {
            ReturnBuffer();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Release();
        }
    }

    internal void TransferPublicOwnership()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            throw new ObjectDisposedException(nameof(PooledBuffer));
        }
    }

    internal Slice LeaseSlice(int offset, int length, int prependLimit)
    {
        if ((uint)offset > (uint)_length || (uint)length > (uint)(_length - offset))
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if ((uint)prependLimit > (uint)offset)
        {
            throw new ArgumentOutOfRangeException(nameof(prependLimit));
        }

        return Slice.CreateRetained(this, offset, length, prependLimit);
    }

    public static implicit operator Span<byte>(PooledBuffer buffer) => buffer.Span;
    public static implicit operator ReadOnlySpan<byte>(PooledBuffer buffer) => buffer.ReadOnlySpan;
    public static implicit operator Memory<byte>(PooledBuffer buffer) => buffer.Memory;
    public static implicit operator ReadOnlyMemory<byte>(PooledBuffer buffer) => buffer.ReadOnlyMemory;

    private void ReturnBuffer()
    {
        ArrayPool<byte> pool = _pool ?? throw new ObjectDisposedException(nameof(PooledBuffer));
        byte[] buffer = _buffer;

        _pool = null;
        _buffer = [];
        _length = 0;
        pool.Return(buffer);
    }

    internal sealed class Lease
    {
        private const int MaxCachedLeases = 2048;
        private static readonly object CachedLeasesLock = new();
        private static readonly Lease?[] CachedLeases = new Lease[MaxCachedLeases];
        private static int _cachedLeaseCount;

        [ThreadStatic]
        private static Lease? t_cachedLease;

        private PooledBuffer? _owner;
        private long _state;

        private Lease()
        {
        }

        internal static (Lease Lease, long Token) TakePublicOwnership(PooledBuffer owner)
        {
            owner.TransferPublicOwnership();
            try
            {
                return Create(owner);
            }
            catch
            {
                owner.Release();
                throw;
            }
        }

        internal static (Lease Lease, long Token) TakeRetained(PooledBuffer owner)
        {
            try
            {
                return Create(owner);
            }
            catch
            {
                owner.Release();
                throw;
            }
        }

        internal static (Lease Lease, long Token) Retain(PooledBuffer owner)
        {
            owner.Retain();
            return TakeRetained(owner);
        }

        internal PooledBuffer GetOwner(long token)
        {
            if (Volatile.Read(ref _state) != token)
            {
                throw new ObjectDisposedException(nameof(PooledBuffer));
            }

            PooledBuffer owner = Volatile.Read(ref _owner) ?? throw new ObjectDisposedException(nameof(PooledBuffer));
            if (Volatile.Read(ref _state) != token)
            {
                throw new ObjectDisposedException(nameof(PooledBuffer));
            }

            return owner;
        }

        internal void Release(long token)
        {
            if (Interlocked.CompareExchange(ref _state, token | 1, token) != token)
            {
                return;
            }

            PooledBuffer? owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release();
            Return(this);
        }

        private static (Lease Lease, long Token) Create(PooledBuffer owner)
        {
            Lease lease = Rent();
            lease._owner = owner;
            long previous = Volatile.Read(ref lease._state);
            long token = unchecked((previous & ~1L) + 2);
            if (token <= 0)
            {
                token = 2;
            }

            Volatile.Write(ref lease._state, token);
            return (lease, token);
        }

        private static Lease Rent()
        {
            Lease? cached = t_cachedLease;
            if (cached is not null)
            {
                t_cachedLease = null;
                return cached;
            }

            lock (CachedLeasesLock)
            {
                int count = _cachedLeaseCount;
                if (count != 0)
                {
                    int index = count - 1;
                    Lease lease = CachedLeases[index]!;
                    CachedLeases[index] = null;
                    _cachedLeaseCount = index;
                    return lease;
                }
            }

            return new Lease();
        }

        private static void Return(Lease lease)
        {
            if (t_cachedLease is null)
            {
                t_cachedLease = lease;
                return;
            }

            lock (CachedLeasesLock)
            {
                int count = _cachedLeaseCount;
                if (count < MaxCachedLeases)
                {
                    CachedLeases[count] = lease;
                    _cachedLeaseCount = count + 1;
                }
            }
        }
    }

    public readonly struct Slice : IDisposable
    {
        private readonly Lease? _lease;
        private readonly long _leaseToken;
        private readonly int _offset;
        private readonly int _length;
        private readonly int _prependLimit;

        private Slice(Lease lease, long leaseToken, int offset, int length, int prependLimit)
        {
            _lease = lease;
            _leaseToken = leaseToken;
            _offset = offset;
            _length = length;
            _prependLimit = prependLimit;
        }

        internal PooledBuffer Owner => _lease?.GetOwner(_leaseToken) ?? throw new ObjectDisposedException(nameof(Slice));
        internal int Offset => _offset;
        internal int PrependLimit => _prependLimit;

        public int Length => _length;
        public int PrependableLength => _offset - _prependLimit;
        public Span<byte> Span => new(Owner._buffer, _offset, _length);
        public ReadOnlySpan<byte> ReadOnlySpan => new(Owner._buffer, _offset, _length);
        public Memory<byte> Memory => new(Owner._buffer, _offset, _length);
        public ReadOnlyMemory<byte> ReadOnlyMemory => new(Owner._buffer, _offset, _length);

        internal Slice Retain()
        {
            (Lease lease, long token) = Lease.Retain(Owner);
            return new Slice(lease, token, _offset, _length, _prependLimit);
        }

        public Slice SliceRange(int offset, int length)
        {
            if ((uint)offset > (uint)_length || (uint)length > (uint)(_length - offset))
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            (Lease lease, long token) = Lease.Retain(Owner);
            int sliceOffset = _offset + offset;
            int prependLimit = offset == 0 ? _prependLimit : sliceOffset;
            return new Slice(lease, token, sliceOffset, length, prependLimit);
        }

        public bool TryPrepend(int length, out Slice slice)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(length);

            if (length > PrependableLength)
            {
                slice = default;
                return false;
            }

            (Lease lease, long token) = Lease.Retain(Owner);
            slice = new Slice(lease, token, _offset - length, _length + length, _prependLimit);
            return true;
        }

        public byte[] ToArray() => ReadOnlySpan.ToArray();

        public void Complete() => Dispose();

        public void Dispose() => _lease?.Release(_leaseToken);

        public static implicit operator Span<byte>(Slice slice) => slice.Span;
        public static implicit operator ReadOnlySpan<byte>(Slice slice) => slice.ReadOnlySpan;
        public static implicit operator Memory<byte>(Slice slice) => slice.Memory;
        public static implicit operator ReadOnlyMemory<byte>(Slice slice) => slice.ReadOnlyMemory;

        internal static Slice TakePublicOwnership(PooledBuffer owner, int offset, int length, int prependLimit)
        {
            (Lease lease, long token) = Lease.TakePublicOwnership(owner);
            return new Slice(lease, token, offset, length, prependLimit);
        }

        internal static Slice CreateRetained(PooledBuffer owner, int offset, int length, int prependLimit)
        {
            (Lease lease, long token) = Lease.Retain(owner);
            return new Slice(lease, token, offset, length, prependLimit);
        }

        internal static Slice TakeLeaseOwnership(Lease lease, long token, int offset, int length, int prependLimit) =>
            new(lease, token, offset, length, prependLimit);
    }
}
