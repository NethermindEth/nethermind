// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Sync.Snap;

/// <summary>
/// ISnapTree adapter for flat snap sync (storage).
/// Owns reader (for IsPersisted) and writeBatch (for commits), disposing them on Dispose.
/// </summary>
public class FlatSnapStorageTree : ISnapTree<PathWithStorageSlot>, SnapBoundaryCleaner.IRangeDeleter
{
    private readonly ILogger _logger;
    private bool _disposed;
    private readonly IPersistence.IPersistenceReader _reader;
    private readonly IPersistence.IAtomicPersistenceBatch _writeBatch;
    private readonly bool _enableDoubleWriteCheck;
    private readonly StorageTree _tree;
    private readonly Hash256 _addressHash;
    private readonly SnapUpperBoundAdapter _adapter;
    private IReadOnlyList<PathWithStorageSlot>? _pendingEntries;
    private (ValueHash256 Start, ValueHash256 End, bool Empty)? _cleanupRangeProbe;
    internal Action? BeforeCleanup { get; set; }

    public FlatSnapStorageTree(IPersistence.IPersistenceReader reader, IPersistence.IAtomicPersistenceBatch writeBatch, Hash256 addressHash, bool enableDoubleWriteCheck, ILogManager logManager)
    {
        _logger = logManager.GetClassLogger<FlatSnapStorageTree>();
        _reader = reader;
        _writeBatch = writeBatch;
        _enableDoubleWriteCheck = enableDoubleWriteCheck;
        _addressHash = addressHash;
        _adapter = new SnapUpperBoundAdapter(new PersistenceStorageTrieStoreAdapter(reader, writeBatch, addressHash, enableDoubleWriteCheck));
        _tree = new StorageTree(_adapter, logManager);
    }

    public Hash256 RootHash => _tree.RootHash;

    public void SetRootFromProof(TrieNode root) => _tree.RootRef = root;

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak)
    {
        ValueHash256 computedHash = default;
        return _reader.TryLoadStorageRlp(_addressHash, path, ReadFlags.None, static (rlp, result) => result[0] = ValueKeccak.Compute(rlp), new Span<ValueHash256>(ref computedHash))
            && computedHash == keccak;
    }

    public void BulkSetAndUpdateRootHash(IReadOnlyList<PathWithStorageSlot> entries)
    {
        _pendingEntries = entries;
        ISnapTree<PathWithStorageSlot>.DoBulkSetAndUpdateRootHash(_tree, entries);
    }

    public void Commit(ValueHash256 upperBound)
    {
        _adapter.UpperBound = upperBound;

        if (_pendingEntries is not null)
        {
            for (int i = 0; i < _pendingEntries.Count; i++)
            {
                PathWithStorageSlot slot = _pendingEntries[i];
                if (slot.Path <= upperBound)
                {
                    if (_enableDoubleWriteCheck)
                    {
                        UInt256 existing = default;
                        if (_reader.TryGetStorageRaw(_addressHash, slot.Path, ref existing))
                            throw new Exception($"Double storage flat write. address:{_addressHash} slot:{slot.Path} firstEntry:{_pendingEntries[0].Path} lastEntry:{_pendingEntries[_pendingEntries.Count - 1].Path} upperBound:{upperBound}");
                    }
                    // slot.SlotRlpValue is already RLP(stripped) — the on-disk format when wrapping is on,
                    // so this avoids a decode + re-encode round-trip.
                    _writeBatch.SetStorageRawEncoded(_addressHash, slot.Path, slot.SlotRlpValue);
                }
            }
        }

        _tree.Commit(writeFlags: WriteFlags.DisableWAL);
        _writeBatch.Commit();
    }

    /// <inheritdoc/>
    public void DeleteOutsideChildren(in TreePath path, TrieNode node, in ValueHash256 cleanupStart, in ValueHash256 cleanupEnd)
    {
        if (!IsCleanupRangeEmpty(cleanupStart, cleanupEnd) && MayHoldData(path, cleanupStart, cleanupEnd))
            SnapBoundaryCleaner.DeleteOutsideChildren(path, node, this, cleanupStart, cleanupEnd);
    }

    /// <inheritdoc/>
    public void DeleteOutsideCompleteTree(in ValueHash256 cleanupStart, in ValueHash256 cleanupEnd)
    {
        if (_tree.RootRef is { } root && !IsCleanupRangeEmpty(cleanupStart, cleanupEnd))
            SnapBoundaryCleaner.DeleteOutsideCompleteTree(TreePath.Empty, root, this, cleanupStart, cleanupEnd);
    }

    /// <summary>
    /// Whether the batch holds no slot and no trie node of this account in <c>[cleanupStart, cleanupEnd]</c>, so cleanup
    /// within those bounds, under any node, could delete nothing.
    /// </summary>
    /// <remarks>
    /// Every range the cleaner deletes, and every single node it deletes, lies within the subtree of the node it cleans
    /// clipped to the cleanup bounds. When the batch's range deletes would find nothing there, the walk could only write
    /// tombstones over keys that do not exist and would never invalidate the checkpoint, so it is skipped. That is every
    /// commit for an account with nothing stored yet, which on a fresh sync is nearly all of them; the walk otherwise
    /// costs a few range scans and dozens of tombstones per slot.
    /// <para>
    /// The probe sees the batch's snapshot, so keys another batch writes after it are not seen. The range deletes miss
    /// them too; only the walk's blind single-node tombstones would have removed one, and only when that other batch
    /// committed first. The snapshot is also why the answer, asked once per commit, holds for every boundary node.
    /// </para>
    /// </remarks>
    private bool IsCleanupRangeEmpty(in ValueHash256 cleanupStart, in ValueHash256 cleanupEnd)
    {
        if (_cleanupRangeProbe is not { } probe || probe.Start != cleanupStart || probe.End != cleanupEnd)
        {
            probe = (cleanupStart, cleanupEnd,
                cleanupStart > cleanupEnd || _writeBatch.IsStorageRangeEmpty(_addressHash.ValueHash256, cleanupStart, cleanupEnd));
            _cleanupRangeProbe = probe;
        }

        return probe.Empty;
    }

    /// <summary>
    /// Whether the batch holds a slot or a trie node of this account that cleanup under <paramref name="path"/> could
    /// delete; see <see cref="IsCleanupRangeEmpty"/>.
    /// </summary>
    private bool MayHoldData(in TreePath path, in ValueHash256 cleanupStart, in ValueHash256 cleanupEnd)
    {
        ValueHash256 from = path.ToLowerBoundPath();
        ValueHash256 to = path.ToUpperBoundPath();
        if (from < cleanupStart) from = cleanupStart;
        if (to > cleanupEnd) to = cleanupEnd;
        return from <= to && !_writeBatch.IsStorageRangeEmpty(_addressHash.ValueHash256, from, to);
    }

    void SnapBoundaryCleaner.IRangeDeleter.DeleteNode(in TreePath path)
    {
        if (BeforeCleanup is not null && _reader.TryLoadStorageRlp(_addressHash, path, ReadFlags.None, static (_, _) => { }, 0))
            InvalidateCheckpoint();
        _writeBatch.DeleteStorageTrieNode(_addressHash, path);
    }

    byte[]? SnapBoundaryCleaner.IRangeDeleter.LoadNode(in TreePath path)
    {
        byte[]? rlp = null;
        _reader.TryLoadStorageRlp(_addressHash, path, ReadFlags.None, static (value, result) => result[0] = value.ToArray(), new Span<byte[]?>(ref rlp));
        return rlp;
    }

    void SnapBoundaryCleaner.IRangeDeleter.DeleteRange(in ValueHash256 from, in ValueHash256 to)
    {
        ValueHash256 addressHash = _addressHash.ValueHash256;
        if (BeforeCleanup is not null)
        {
            using IPersistence.IFlatIterator slots = _reader.CreateStorageIterator(addressHash, from, to);
            if (slots.MoveNext()) InvalidateCheckpoint();
        }
        _writeBatch.DeleteStorageRange(addressHash, from, to);
        _writeBatch.DeleteStorageTrieNodeRange(addressHash, from, to, BeforeCleanup);
    }

    private void InvalidateCheckpoint()
    {
        BeforeCleanup?.Invoke();
        BeforeCleanup = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _writeBatch.Dispose();
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error("Failed to release the storage transaction.", exception);
        }
        try
        {
            _reader.Dispose();
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error("Failed to release the storage reader.", exception);
        }
    }
}
