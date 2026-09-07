// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>Backing store used by <see cref="TrieUpdater"/> for canonical complete-key mutations.</summary>
/// <remarks>
/// Reads observe the current tree. <see cref="Apply"/> publishes all leaf and node changes together;
/// implementations must leave the store unchanged when it throws.
/// </remarks>
public interface IPbtStore
{
    /// <summary>Gets the encoded canonical node at <paramref name="path"/>, or <see langword="null"/> when absent.</summary>
    byte[]? GetNode(PbtNodePath path);

    /// <summary>Gets a caller-owned lease for the complete group containing <paramref name="groupKey"/>.</summary>
    /// <remarks>
    /// A non-null result transfers exactly one reference to the caller, which must invoke
    /// <see cref="IDisposable.Dispose"/> exactly once. Consumers must treat the memory returned by
    /// <see cref="RefCountingMemory.GetSpan"/> as read-only. The reference keeps the payload valid until
    /// released, independently of the store. A missing group returns <see langword="null"/>. Implementations
    /// validate that the key is at a four-level boundary even when the group is absent.
    /// </remarks>
    RefCountingMemory? GetNodeGroup(PbtNodePath groupKey)
    {
        ArgumentNullException.ThrowIfNull(groupKey);
        if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
            throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(groupKey));

        List<PbtNodeRecord> records = [];
        for (int position = 0; position < PbtNodeGroupCodec.PositionCount; position++)
        {
            if (position == PbtFourLevelGroupGeometry.RootPosition && groupKey.BitDepth != 0) continue;
            PbtNodePath path = PbtFourLevelGroupGeometry.PathOf(groupKey, position);
            byte[]? encoding = GetNode(path);
            if (encoding is not null) records.Add(new PbtNodeRecord(path, encoding));
        }

        if (records.Count == 0) return null;
        BufferWriter writer = new(PooledRefCountingMemoryProvider.Instance);
        try
        {
            PbtNodeGroupCodec.Encode(ref writer, groupKey, records);
            return writer.Detach()!;
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    /// <summary>Writes or deletes a complete-key leaf.</summary>
    void SetLeaf(PbtFullKey key, ValueHash256? value);

    /// <summary>Writes or deletes the canonical node at <paramref name="path"/>.</summary>
    void SetNode(PbtNodePath path, byte[]? encoding);
}
