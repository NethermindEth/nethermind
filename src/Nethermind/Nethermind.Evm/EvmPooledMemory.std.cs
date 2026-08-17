// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Collections;

namespace Nethermind.Evm;

public partial struct EvmPooledMemory
{
    /// <summary>Binds this frame's memory to the VM's shared buffer at <paramref name="baseOffset"/>.</summary>
    internal void AttachShared(SharedEvmMemory shared, int baseOffset)
    {
        _shared = shared;
        _base = baseOffset;
        _offset = baseOffset;
    }

    /// <summary>Where a child frame's window starts; a spilled frame occupies no shared space.</summary>
    internal int FrameFrontier => _spilled ? _base : _base + (int)Size;

    private void ClearForTracing(ulong size)
    {
        if (_memory is null || size <= _lastZeroedSize)
        {
            return;
        }

        int frameOld = (int)_lastZeroedSize;
        int frameNew = (int)Math.Min(size, (ulong)(_memory.Length - _offset));
        if (frameNew <= frameOld)
        {
            return;
        }

        if (_shared is not null && !_spilled)
        {
            _shared.Zero(_base, frameOld, frameNew);
            // Children anchor at _base + Size; recording a tracing over-clear beyond Size as clean would
            // make the parent skip re-zeroing a later sibling's writes there. Keep _lastZeroedSize <= Size.
            _lastZeroedSize = Math.Min((ulong)frameNew, Size);
        }
        else
        {
            Array.Clear(_memory, _offset + frameOld, frameNew - frameOld);
            _lastZeroedSize = (ulong)frameNew;
        }
    }

    public void Dispose()
    {
        byte[]? memory = _memory;
        if (memory is null)
        {
            return;
        }

        _memory = null;
        // The shared buffer belongs to the VM and is never returned — its window is reused by the next
        // sibling frame, whose growth re-zeroes stale bytes. Only a private/spilled buffer goes back.
        if (_shared is null || _spilled)
        {
            ReturnLarge(memory);
        }
    }

    private const int MaxSharedArrayLength = 1 << 20;
    // Above this, buffers fall back to plain allocation (not pooled), as before this change.
    private const int MaxLargePooledArrayLength = 1 << 22;
    private static readonly System.Buffers.ArrayPool<byte> _largeArrayPool =
        System.Buffers.ArrayPool<byte>.Create(maxArrayLength: MaxLargePooledArrayLength, maxArraysPerBucket: 16);

    private static byte[] RentLarge(int minLength)
        => minLength > MaxSharedArrayLength
            ? _largeArrayPool.Rent(minLength)
            : SafeArrayPool<byte>.Shared.Rent(minLength);

    private static void ReturnLarge(byte[] array)
    {
        if (array.Length > MaxSharedArrayLength)
            _largeArrayPool.Return(array);
        else
            SafeArrayPool<byte>.Shared.Return(array);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RentSlow()
    {
        // Unattached (bare construction) or already spilled → grow a private pooled array by realloc-copy.
        if (_shared is null || _spilled)
        {
            RentPrivate();
            return;
        }

        byte[] buffer = _shared.Buffer;
        // Spill to a private array if the window [_base, _base + Size) can't fit the fixed reserve.
        if ((ulong)_base + Size > (ulong)buffer.Length)
        {
            SpillToPrivate();
            return;
        }

        _memory = buffer; // _offset == _base, set in AttachShared
        _shared.Zero(_base, (int)_lastZeroedSize, (int)Size);
        _lastZeroedSize = Size;
    }

    private void RentPrivate()
    {
        if (_memory is null)
        {
            _memory = RentLargeCleared((int)Math.Max((uint)Size, MinRentSize));
        }
        else if (Size > (ulong)_memory.LongLength)
        {
            byte[] beforeResize = _memory;
            _memory = RentLargeCleared(TruncateToInt32(Size));
            Array.Copy(beforeResize, 0, _memory, 0, beforeResize.Length);
            ReturnLarge(beforeResize);
        }

        _offset = 0;
        _lastZeroedSize = (ulong)_memory.Length;
    }

    // Rare: the frame outgrew the reserve. Move to a private array, preserving the bytes already written.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SpillToPrivate()
    {
        byte[] priv = RentLargeCleared(TruncateToInt32(Size));
        int copyLen = (int)Math.Min(_lastZeroedSize, Size);
        if (_memory is not null && copyLen > 0)
        {
            Array.Copy(_memory, _base, priv, 0, copyLen);
        }

        _memory = priv;
        _offset = 0;
        _spilled = true;
        _lastZeroedSize = (ulong)priv.Length;
    }

    private static byte[] RentLargeCleared(int minLength)
    {
        byte[] array = RentLarge(minLength);
        Array.Clear(array);
        return array;
    }
}
