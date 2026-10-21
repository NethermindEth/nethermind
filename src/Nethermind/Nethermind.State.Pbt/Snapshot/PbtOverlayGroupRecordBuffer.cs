// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Memory;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;

namespace Nethermind.State.Pbt.Snapshot;

/// <summary>A group record buffer that reads a pinned global buffer and keeps its own writes in a private buffer.</summary>
/// <remarks>
/// References below the write boundary of the global pin taken at construction resolve in the global buffer, and the rest in a
/// private <see cref="PbtGroupRecordBuffer"/> that takes every write. Private generations are numbered after the pinned global one, so
/// the two reference ranges never overlap and the private records are freed with this buffer instead of growing the global one.
/// A global record written after the pin is out of reach, so a snapshot root there maps to Unknown. Every pin of this buffer also
/// holds the global pin, so the global records it reads outlive <see cref="Dispose"/> until its last pin is released.
/// </remarks>
public sealed class PbtOverlayGroupRecordBuffer : IPbtGroupRecordBuffer, IDisposable
{
    private const int LocalGenerationBytes = 128 * 1024;

    private readonly IPbtGroupRecordBuffer _global;
    private readonly PbtGroupRecordBuffer _local;
    private readonly IPbtGroupRecordBufferPin _globalPin;
    private readonly PbtGroupReference _globalBoundary;
    private readonly uint _generationShift;

    public PbtOverlayGroupRecordBuffer(IPbtGroupRecordBuffer global, IRefCountingMemoryProvider memoryProvider)
    {
        _global = global;
        _local = new PbtGroupRecordBuffer(memoryProvider, compressedMaxMemory: 0, LocalGenerationBytes);
        _globalPin = global.PinHead();
        _globalBoundary = _globalPin.WriteBoundary;
        _generationShift = _globalBoundary.Generation;
    }

    /// <inheritdoc/>
    public IPbtGroupRecordBufferPin PinHead() => CombinePin(_local.PinHead());

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
    public bool TryLocate(PbtGroupReference reference, out PbtGroupRecord record)
    {
        if (!IsLocal(reference)) return _global.TryLocate(reference, out record);
        if (!_local.TryLocate(ToLocal(reference), out record)) return false;
        record = record.WithReference(reference);
        return true;
    }

    /// <inheritdoc/>
    public PbtGroupReference Reserve(PbtStorageNodePath path, int payloadLength, in PbtGroupLinks links, out Span<byte> payload) =>
        FromLocal(_local.Reserve(path, payloadLength, links, out payload));

    /// <inheritdoc/>
    public PbtGroupReference FindRoot(in ValueHash256 root)
    {
        PbtGroupReference reference = _local.FindRoot(root);
        if (reference.IsDirect) return FromLocal(reference);

        // A root the global buffer indexed after the pin would resolve in the private buffer instead.
        reference = _global.FindRoot(root);
        return IsLocal(reference) ? PbtGroupReference.Unknown : reference;
    }

    /// <inheritdoc/>
    /// <remarks>A root among the global records is not indexed, as that would write to the global buffer.</remarks>
    public void SetRoot(in ValueHash256 root, PbtGroupReference reference)
    {
        if (!reference.IsDirect) throw new ArgumentException("Only a published root group can be indexed.", nameof(reference));
        if (IsLocal(reference)) _local.SetRoot(root, ToLocal(reference));
    }

    /// <inheritdoc/>
    public PbtGroupReference MapReference(IPbtGroupRecordBuffer source, PbtGroupReference reference)
    {
        if (ReferenceEquals(source, this)) return reference;
        if (!ReferenceEquals(source, _global)) throw new InvalidOperationException("A branch must share the group record buffer of its in-memory snapshots.");
        return IsLocal(reference) ? PbtGroupReference.Unknown : reference;
    }

    /// <summary>Stops new pins and writes; the private records and the global pin are released once every pin of this buffer is.</summary>
    public void Dispose()
    {
        _local.Dispose();
        _globalPin.Dispose();
    }

    private bool IsLocal(PbtGroupReference reference) => reference.IsDirect && reference.CompareTo(_globalBoundary) >= 0;

    private PbtGroupReference ToLocal(PbtGroupReference reference) => new(reference.Generation - _generationShift, reference.Offset);

    private PbtGroupReference FromLocal(PbtGroupReference reference) =>
        reference.IsDirect ? new(reference.Generation + _generationShift, reference.Offset) : reference;

    private OverlayPin CombinePin(IPbtGroupRecordBufferPin localPin)
    {
        try
        {
            return new OverlayPin(this, localPin, _globalPin.Copy());
        }
        catch
        {
            localPin.Dispose();
            throw;
        }
    }

    private sealed class OverlayPin(PbtOverlayGroupRecordBuffer buffer, IPbtGroupRecordBufferPin localPin, IPbtGroupRecordBufferPin globalPin) : IPbtGroupRecordBufferPin
    {
        public IPbtGroupRecordBuffer Buffer => buffer;

        public PbtGroupReference WriteBoundary => buffer.FromLocal(localPin.WriteBoundary);

        public IPbtGroupRecordBufferPin Copy() => buffer.CombinePin(localPin.Copy());

        public void Dispose()
        {
            localPin.Dispose();
            globalPin.Dispose();
        }
    }
}
