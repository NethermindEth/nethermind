// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Trie.NodeBuffer;

namespace Nethermind.State.Flat.NodeBuffer;

/// <summary>A node-record buffer that also resolves the transient pointers of a <see cref="WarmedNodeBuffer"/>.</summary>
/// <remarks>
/// Writes, pins and roots go to <paramref name="global"/>; the warmed buffer keeps its own records readable until it is reset.
/// Owns neither, so <see cref="Dispose"/> does nothing.
/// </remarks>
internal sealed class WarmedNodeRecordBuffer(INodeRecordBuffer global, WarmedNodeBuffer warmed) : INodeRecordBuffer
{
    /// <inheritdoc/>
    public INodeRecordBufferPin PinHead() => global.PinHead();

    /// <inheritdoc/>
    public IDisposable PinAll() => global.PinAll();

    /// <inheritdoc/>
    public bool TryLocate(ulong pointer, out NodeRecord record) =>
        NodeRecordCodec.IsTransient(pointer) ? warmed.TryLocate(pointer, out record) : global.TryLocate(pointer, out record);

    /// <inheritdoc/>
    public INodeRecordWriteBatch StartWriteBatch(int alignedTotalLength) => global.StartWriteBatch(alignedTotalLength);

    /// <inheritdoc/>
    public ulong FindRoot(Hash256? address, in ValueHash256 root) => global.FindRoot(address, root);

    /// <inheritdoc/>
    public void SetRoot(Hash256? address, in ValueHash256 root, ulong rootPointer) => global.SetRoot(address, root, rootPointer);

    public void Dispose() { }
}
