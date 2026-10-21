// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt.NodeBuffer;

/// <remarks>
/// The root index maps tree roots to the records of their root groups, so a branch whose base is no longer in memory
/// can still start from a resident root. A slot keeps the newest root hashing to it; an entry whose record has been
/// reclaimed reads as a miss.
/// </remarks>
public sealed partial class PbtGroupRecordBuffer
{
    private const int RootSlots = 1024;

    private readonly (ValueHash256 Root, PbtGroupReference Reference)[] _roots = new (ValueHash256, PbtGroupReference)[RootSlots];

    /// <inheritdoc/>
    /// <remarks>The entry replaces its slot's previous one.</remarks>
    public void SetRoot(in ValueHash256 root, PbtGroupReference reference)
    {
        if (!reference.IsDirect) throw new ArgumentException("Only a published root group can be indexed.", nameof(reference));
        lock (_lock)
        {
            ThrowIfDisposed();
            _roots[RootSlot(root)] = (root, reference);
        }
    }

    /// <inheritdoc/>
    /// <remarks>A resident record may still be reclaimed after this call; a later miss resolves through the persisted view.</remarks>
    public PbtGroupReference FindRoot(in ValueHash256 root)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            (ValueHash256 indexed, PbtGroupReference reference) = _roots[RootSlot(root)];
            return indexed == root && TryLocateBytes(reference, out _) ? reference : PbtGroupReference.Unknown;
        }
    }

    private static int RootSlot(in ValueHash256 root) => (int)(MemoryMarshal.Read<ulong>(root.Bytes) & (RootSlots - 1));
}
