// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Pbt.NodeBuffer;

namespace Nethermind.State.Pbt.NodeBuffer;

/// <summary>A group record buffer that reads a pinned global buffer and keeps its own writes in a private buffer.</summary>
/// <remarks>
/// Pointers below the boundary of the global pin taken at construction resolve in the global buffer, and the rest in a private
/// <see cref="PbtPrivateGroupRecordBuffer"/> that takes every write. Private generations are exposed numbered after the pinned global one,
/// so the two pointer ranges never overlap and the private records are freed with this buffer instead of growing the global one.
/// Every pin of this buffer also holds the global pin, so the global records it reads outlive <see cref="Dispose"/> until its last pin is released.
/// </remarks>
public sealed class PbtOverlayGroupRecordBuffer : IPbtGroupRecordBuffer
{
    private const int LocalGenerationBytes = 128 * 1024;

    private readonly IPbtGroupRecordBuffer _global;
    private readonly IPbtGroupRecordBufferPin _globalPin;
    private readonly IPbtGroupRecordBuffer _local = new PbtPrivateGroupRecordBuffer(LocalGenerationBytes);
    private readonly ulong _globalBoundary;
    private readonly ulong _localOffset;

    public PbtOverlayGroupRecordBuffer(IPbtGroupRecordBuffer global)
    {
        _global = global;
        _globalPin = global.PinHead();
        _globalBoundary = _globalPin.WriteBoundary;
        _localOffset = _globalBoundary >> PbtGroupRecordCodec.OffsetBits << PbtGroupRecordCodec.OffsetBits;
    }

    /// <inheritdoc/>
    public IPbtGroupRecordBufferPin PinHead() => CombinePin(_local.PinHead(), _globalPin);

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
    public bool TryLocate(ulong pointer, out PbtGroupRecord record) =>
        IsLocal(pointer) ? _local.TryLocate(pointer - _localOffset, out record) : _global.TryLocate(pointer, out record);

    /// <inheritdoc/>
    public IPbtGroupRecordWriteBatch StartWriteBatch(int alignedTotalLength) => new LocalWriteBatch(_local.StartWriteBatch(alignedTotalLength), _localOffset);

    /// <inheritdoc/>
    public ulong FindRoot(in ValueHash256 root)
    {
        ulong pointer = _local.FindRoot(root);
        if (pointer != 0) return pointer + _localOffset;

        // A root the global buffer indexed after the pin would resolve in the private buffer instead.
        pointer = _global.FindRoot(root);
        return IsLocal(pointer) ? 0 : pointer;
    }

    /// <inheritdoc/>
    /// <remarks>A root among the global records is not indexed, as that would write to the global buffer.</remarks>
    public void SetRoot(in ValueHash256 root, ulong rootPointer)
    {
        if (IsLocal(rootPointer)) _local.SetRoot(root, rootPointer - _localOffset);
    }

    /// <summary>Stops new operations; the private records and the global pin are released once every pin of this buffer is.</summary>
    public void Dispose()
    {
        _local.Dispose();
        _globalPin.Dispose();
    }

    private bool IsLocal(ulong pointer) => pointer >= _globalBoundary && !PbtGroupRecordCodec.IsTransient(pointer);

    private OverlayPin CombinePin(IPbtGroupRecordBufferPin localPin, IPbtGroupRecordBufferPin globalPin)
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

    private sealed class LocalWriteBatch(IPbtGroupRecordWriteBatch local, ulong localOffset) : IPbtGroupRecordWriteBatch
    {
        public ulong BasePointer => local.BasePointer + localOffset;

        public Span<byte> Bytes => local.Bytes;

        public void Dispose() => local.Dispose();
    }

    private sealed class OverlayPin(PbtOverlayGroupRecordBuffer buffer, IPbtGroupRecordBufferPin localPin, IPbtGroupRecordBufferPin globalPin) : IPbtGroupRecordBufferPin
    {
        public IPbtGroupRecordBuffer Buffer => buffer;

        public ulong WriteBoundary => localPin.WriteBoundary + buffer._localOffset;

        public IPbtGroupRecordBufferPin Copy() => buffer.CombinePin(localPin.Copy(), globalPin);

        public void Dispose()
        {
            localPin.Dispose();
            globalPin.Dispose();
        }
    }
}
