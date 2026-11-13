// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie.NodeBuffer;

namespace Nethermind.State.Flat.NodeBuffer;

/// <summary>A node-record buffer that reads a pinned global buffer and keeps its own writes in a private buffer.</summary>
/// <remarks>
/// Pointers below the boundary of the global pin taken at construction resolve in the global buffer, and the rest in a private
/// <see cref="PrivateNodeRecordBuffer"/> that takes every write. Private generations are exposed numbered after the pinned global one,
/// so the two pointer ranges never overlap and the private records are freed with this buffer instead of growing the global one.
/// Every pin of this buffer also holds the global pin, so the global records it reads outlive <see cref="Dispose"/> until its last pin is released.
/// </remarks>
internal sealed class OverlayNodeRecordBuffer : INodeRecordBuffer
{
    private const int LocalGenerationBytes = 128 * 1024;

    private readonly INodeRecordBuffer _global;
    private readonly INodeRecordBufferPin _globalPin;
    private readonly INodeRecordBuffer _local = new PrivateNodeRecordBuffer(LocalGenerationBytes);
    private readonly ulong _globalBoundary;
    private readonly ulong _localOffset;

    public OverlayNodeRecordBuffer(INodeRecordBuffer global)
    {
        _global = global;
        _globalPin = global.PinHead();
        _globalBoundary = _globalPin.WriteBoundary;
        _localOffset = _globalBoundary >> NodeRecordBuffer.OffsetBits << NodeRecordBuffer.OffsetBits;
    }

    /// <inheritdoc/>
    public INodeRecordBufferPin PinHead() => CombinePin(_local.PinHead(), _globalPin);

    /// <inheritdoc/>
    public IDisposable PinAll()
    {
        IDisposable localPin = _local.PinAll();
        try
        {
            return new CompositeDisposable { localPin, _global.PinAll() };
        }
        catch
        {
            localPin.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public bool TryLocate(ulong pointer, out NodeRecord record) =>
        IsLocal(pointer) ? _local.TryLocate(pointer - _localOffset, out record) : _global.TryLocate(pointer, out record);

    /// <inheritdoc/>
    public INodeRecordWriteBatch StartWriteBatch(int alignedTotalLength) => new LocalWriteBatch(_local.StartWriteBatch(alignedTotalLength), _localOffset);

    /// <inheritdoc/>
    public ulong FindRoot(Hash256? address, in ValueHash256 root, ulong boundary)
    {
        if (boundary > _globalBoundary)
        {
            ulong pointer = _local.FindRoot(address, root, boundary - _localOffset);
            if (pointer != 0) return pointer + _localOffset;
        }

        // The global buffer may index the same root again after the pin; the entry visible at the pin is still pinned.
        return _global.FindRoot(address, root, Math.Min(boundary, _globalBoundary));
    }

    /// <inheritdoc/>
    /// <remarks>A root among the global records is not indexed, as that would write to the global buffer.</remarks>
    public void SetRoot(Hash256? address, in ValueHash256 root, ulong rootPointer)
    {
        if (IsLocal(rootPointer)) _local.SetRoot(address, root, rootPointer - _localOffset);
    }

    /// <summary>Stops new operations; the private records and the global pin are released once every pin of this buffer is.</summary>
    public void Dispose()
    {
        _local.Dispose();
        _globalPin.Dispose();
    }

    private bool IsLocal(ulong pointer) => pointer >= _globalBoundary && !NodeRecordCodec.IsTransient(pointer);

    private OverlayPin CombinePin(INodeRecordBufferPin localPin, INodeRecordBufferPin globalPin)
    {
        try
        {
            return new OverlayPin(this, localPin, globalPin.Copy());
        }
        catch
        {
            localPin.Dispose();
            throw;
        }
    }

    private sealed class LocalWriteBatch(INodeRecordWriteBatch local, ulong localOffset) : INodeRecordWriteBatch
    {
        public ulong BasePointer => local.BasePointer + localOffset;

        public Span<byte> Bytes => local.Bytes;

        public void Dispose() => local.Dispose();
    }

    private sealed class OverlayPin(OverlayNodeRecordBuffer buffer, INodeRecordBufferPin localPin, INodeRecordBufferPin globalPin) : INodeRecordBufferPin
    {
        public INodeRecordBuffer Buffer => buffer;

        public ulong WriteBoundary => localPin.WriteBoundary + buffer._localOffset;

        public INodeRecordBufferPin Copy() => buffer.CombinePin(localPin.Copy(), globalPin);

        public void Dispose()
        {
            localPin.Dispose();
            globalPin.Dispose();
        }
    }
}
