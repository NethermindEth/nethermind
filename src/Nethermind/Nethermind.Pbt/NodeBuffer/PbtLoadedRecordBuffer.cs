// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Pbt.NodeBuffer;

/// <summary>The payloads of the groups a traversal loads from an <see cref="IPbtStore"/>, readable until <see cref="Clear"/>.</summary>
/// <remarks>
/// Copied payloads are written to pooled chunks that never move, so a payload handed out stays valid as more are added.
/// A value a store lends as native memory is not copied but kept pinned until <see cref="Clear"/>.
/// </remarks>
public sealed class PbtLoadedRecordBuffer : IDisposable
{
    private const int ChunkBytes = 64 * 1024;

    /// <summary>Copies the bytes handed to it into the target's buffer, for a source that lends them only for the callback.</summary>
    public static readonly ReadOnlySpanAction<byte, CopyTarget> Copy = static (bytes, target) => target.Payload = target.Buffer.Add(bytes);

    private readonly List<byte[]> _chunks = [];
    private int _used = ChunkBytes;
    /// <summary>The pinned values, then released managers kept for the next pins.</summary>
    private readonly List<PinnedSlice> _pinned = [];
    private int _pinnedCount;

    /// <summary>Reads the value of <paramref name="key"/>, pinned in place when <paramref name="store"/> lends native memory, otherwise copied.</summary>
    /// <returns>False when the store has no such key.</returns>
    public bool TryRead(IReadOnlyKeyValueStore store, scoped ReadOnlySpan<byte> key, out ReadOnlyMemory<byte> value)
    {
        value = default;
        if (store is not IReadOnlyNativeKeyValueStore native) return store.TryGet(key, Copy, new CopyTarget(this, ref value));

        ReadOnlySpan<byte> slice = native.GetNativeSlice(key, out nint handle);
        if (slice.IsNull()) return false;

        if (_pinnedCount == _pinned.Count) _pinned.Add(new PinnedSlice());
        PinnedSlice pinned = _pinned[_pinnedCount++];
        pinned.Pin(native, handle, slice);
        value = pinned.Memory;
        return true;
    }

    /// <summary>Releases every payload, which must no longer be read.</summary>
    public void Clear()
    {
        foreach (byte[] chunk in _chunks) ArrayPool<byte>.Shared.Return(chunk);
        _chunks.Clear();
        _used = ChunkBytes;
        for (; _pinnedCount > 0; _pinnedCount--) _pinned[_pinnedCount - 1].Release();
    }

    public void Dispose() => Clear();

    private ReadOnlyMemory<byte> Add(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > ChunkBytes - _used)
        {
            _chunks.Add(ArrayPool<byte>.Shared.Rent(Math.Max(ChunkBytes, payload.Length)));
            _used = 0;
        }

        Memory<byte> destination = _chunks[^1].AsMemory(_used, payload.Length);
        payload.CopyTo(destination.Span);
        _used += payload.Length;
        return destination;
    }

    /// <summary>Where <see cref="Copy"/> puts the copy.</summary>
    public readonly ref struct CopyTarget
    {
        public readonly PbtLoadedRecordBuffer Buffer;
        public readonly ref ReadOnlyMemory<byte> Payload;

        public CopyTarget(PbtLoadedRecordBuffer buffer, ref ReadOnlyMemory<byte> payload)
        {
            Buffer = buffer;
            Payload = ref payload;
        }
    }

    /// <summary>The memory of a value a store lends pinned, until <see cref="Release"/> hands it back.</summary>
    private sealed unsafe class PinnedSlice : MemoryManager<byte>
    {
        private IReadOnlyNativeKeyValueStore? _store;
        private nint _handle;
        private void* _pointer;
        private int _length;

        public void Pin(IReadOnlyNativeKeyValueStore store, nint handle, ReadOnlySpan<byte> slice)
        {
            _store = store;
            _handle = handle;
            _pointer = Unsafe.AsPointer(ref MemoryMarshal.GetReference(slice));
            _length = slice.Length;
        }

        public void Release()
        {
            IReadOnlyNativeKeyValueStore store = _store!;
            _store = null;
            _pointer = null;
            _length = 0;
            store.DangerousReleaseHandle(_handle);
        }

        public override Span<byte> GetSpan() => new(_pointer, _length);

        public override MemoryHandle Pin(int elementIndex = 0) => new((byte*)_pointer + elementIndex);

        public override void Unpin() { }

        protected override void Dispose(bool disposing) { }
    }
}
