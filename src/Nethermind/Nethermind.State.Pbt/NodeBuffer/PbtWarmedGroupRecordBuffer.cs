// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Pbt.NodeBuffer;

namespace Nethermind.State.Pbt.NodeBuffer;

/// <summary>A group record buffer that also resolves the transient pointers of a <see cref="PbtWarmedGroupBuffer"/>.</summary>
/// <remarks>
/// Writes, pins and roots go to <paramref name="global"/>; the warmed buffer keeps its own records readable until it is reset.
/// Owns neither, so <see cref="Dispose"/> does nothing.
/// </remarks>
public sealed class PbtWarmedGroupRecordBuffer(IPbtGroupRecordBuffer global, PbtWarmedGroupBuffer warmed) : IPbtGroupRecordBuffer
{
    /// <inheritdoc/>
    public IPbtGroupRecordBufferPin PinHead() => global.PinHead();

    /// <inheritdoc/>
    public IDisposable PinAll() => global.PinAll();

    /// <inheritdoc/>
    public bool TryLocate(ulong pointer, out PbtGroupRecord record) =>
        PbtGroupRecordCodec.IsTransient(pointer) ? warmed.TryLocate(pointer, out record) : global.TryLocate(pointer, out record);

    /// <inheritdoc/>
    public IPbtGroupRecordWriteBatch StartWriteBatch(int alignedTotalLength) => global.StartWriteBatch(alignedTotalLength);

    /// <inheritdoc/>
    public ulong FindRoot(in ValueHash256 root) => global.FindRoot(root);

    /// <inheritdoc/>
    public void SetRoot(in ValueHash256 root, ulong rootPointer) => global.SetRoot(root, rootPointer);

    public void Dispose() { }
}
