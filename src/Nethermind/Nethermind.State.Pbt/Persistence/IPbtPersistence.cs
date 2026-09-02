// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Persistence;

/// <summary>Durable atomic storage for one canonical EIP-8297 state.</summary>
public interface IPbtPersistence
{
    IReader CreateReader();

    IWriteBatch CreateWriteBatch(in StateId from, in StateId to, in ValueHash256 treeRoot, WriteFlags flags);
    IWriteBatch CreateStagingWriteBatch(WriteFlags flags);

    void Flush();

    public interface IReader : IDisposable
    {
        StateId CurrentState { get; }
        ValueHash256 CurrentRoot { get; }

        ValueHash256? GetLeaf(PbtFullKey key);
        IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeaves();
        IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeaves(PbtFullKey prefix);

        byte[]? GetNode(PbtNodePath path);

        /// <summary>Gets an owned, read-only lease for the complete group identified by <paramref name="groupKey"/>.</summary>
        /// <remarks>
        /// The caller must dispose a non-null result. The lease keeps its payload valid independently of
        /// the reader until disposal; its memory is invalid after disposal. A missing group returns
        /// <see langword="null"/>. Implementations validate that <paramref name="groupKey"/> is at a
        /// four-level boundary even when the group is absent.
        /// </remarks>
        /// <param name="groupKey">The four-level-boundary key identifying the group.</param>
        /// <returns>An owned payload lease, or <see langword="null"/> when the group is absent.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="groupKey"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="groupKey"/> is not at a four-level boundary.</exception>
        PbtNodeGroupPayload? GetNodeGroup(PbtNodePath groupKey)
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
                return PbtNodeGroupPayload.FromLease(writer.Detach()!);
            }
            catch
            {
                writer.Dispose();
                throw;
            }
        }

        IEnumerable<KeyValuePair<PbtNodePath, byte[]>> EnumerateNodes();

        ulong GetCodeReference(in ValueHash256 codeHash);
    }

    public interface IWriteBatch : IDisposable
    {
        void SetLeaf(PbtFullKey key, ValueHash256? value);
        void SetNode(PbtNodePath path, ReadOnlySpan<byte> encoding);
        void SetCodeReference(in ValueHash256 codeHash, ulong? referenceCount);
        void Commit();
    }
}
