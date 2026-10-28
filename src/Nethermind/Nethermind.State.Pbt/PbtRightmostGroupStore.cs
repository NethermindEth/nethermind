// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;
using Nethermind.State.Pbt.NodeBuffer;

namespace Nethermind.State.Pbt;

/// <summary>
/// Node-group store for folding strictly ascending leaves in windows from an empty tree; between windows it retains only the
/// tree's right spine and hands every group record of the tree to <paramref name="writeThrough"/>.
/// </summary>
/// <remarks>
/// A key above every folded key reaches an existing group only when that group's path prefixes the largest folded key, so a
/// later window reads only the groups on the right spine, each by pointer from the root.
/// </remarks>
/// <param name="writeThrough">Receives every group record of the tree once, borrowed for the call, one call at a time.</param>
public sealed class PbtRightmostGroupStore(Action<PbtGroupRecord> writeThrough) : IPbtStore, IDisposable
{
    private readonly PbtPrivateGroupRecordBuffer _buffer = new(64 << 20);
    /// <summary>The pointer of the last retained spine record; every record past it was written by the current window.</summary>
    private ulong _retainedBoundary;

    public bool TryLocate(ulong pointer, out PbtGroupRecord record) => _buffer.TryLocate(pointer, out record);

    public IPbtGroupRecordWriteBatch StartWriteBatch(int alignedTotalLength) => _buffer.StartWriteBatch(alignedTotalLength);

    /// <remarks>Misses: a window reaches every group it reads by pointer.</remarks>
    public bool TryLoad<TArg>(scoped in PbtTraversalPath path, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct => false;

    /// <summary>Writes the window's groups through and drops all but the right spine; call once each window's fold ends.</summary>
    /// <remarks>
    /// Any change rewrites every ancestor of the changed group, so a record retained from the previous window roots a subtree
    /// already written through. The retained spine records keep only the link to their spine child: a later window never
    /// follows the others.
    /// </remarks>
    /// <param name="rootPointer">The pointer of the window's root record.</param>
    /// <returns>The pointer of the retained root record.</returns>
    public ulong CompleteWindow(ulong rootPointer)
    {
        WriteThrough(rootPointer);

        List<(byte[] Record, int SpineSlot)> spine = [];
        for (ulong pointer = rootPointer; pointer != 0;)
        {
            PbtGroupRecord record = Locate(pointer);
            int spineSlot = PbtFourLevelGroupGeometry.BoundarySlots - 1;
            while (spineSlot >= 0 && record.GetLink(spineSlot) == 0) spineSlot--;
            spine.Add((record.Span.ToArray(), spineSlot));
            pointer = spineSlot < 0 ? 0 : record.GetLink(spineSlot);
        }

        _buffer.Clear();
        int totalLength = 0;
        foreach ((byte[] record, _) in spine) totalLength += PbtGroupRecordCodec.AlignedLength(record.Length);

        // Laid out deepest first, so the root's pointer is the largest retained one.
        using IPbtGroupRecordWriteBatch batch = _buffer.StartWriteBatch(totalLength);
        int offset = 0;
        ulong childPointer = 0;
        for (int depth = spine.Count - 1; depth >= 0; depth--)
        {
            (byte[] bytes, int spineSlot) = spine[depth];
            PbtGroupRecord record = new(bytes, 0);
            PbtGroupLinks links = default;
            if (spineSlot >= 0) links[spineSlot] = childPointer;
            ReadOnlySpan<byte> payload = record.Payload.Span;
            payload.CopyTo(PbtGroupRecordCodec.WriteGroup(batch.Bytes[offset..], record.Path, links, payload.Length));
            childPointer = batch.BasePointer + (ulong)offset;
            offset += PbtGroupRecordCodec.AlignedLength(bytes.Length);
        }

        return _retainedBoundary = childPointer;
    }

    public void Dispose() => _buffer.Dispose();

    private void WriteThrough(ulong pointer)
    {
        PbtGroupRecord record = Locate(pointer);
        writeThrough(record);
        for (int slot = 0; slot < PbtFourLevelGroupGeometry.BoundarySlots; slot++)
        {
            ulong link = record.GetLink(slot);
            if (link > _retainedBoundary) WriteThrough(link);
        }
    }

    private PbtGroupRecord Locate(ulong pointer) =>
        _buffer.TryLocate(pointer, out PbtGroupRecord record) ? record : throw new InvalidDataException("A linked PBT node group is missing from the buffer.");
}
