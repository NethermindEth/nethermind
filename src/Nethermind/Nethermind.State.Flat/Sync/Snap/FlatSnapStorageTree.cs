// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.Sync.Snap;

/// <summary>
/// ISnapTree adapter for flat snap sync (storage).
/// Owns reader (for IsPersisted) and writeBatch (for commits), disposing them on Dispose.
/// </summary>
public class FlatSnapStorageTree : ISnapTree<PathWithStorageSlot>
{
    private readonly IPersistence.IPersistenceReader _reader;
    private readonly IPersistence.IWriteBatch _writeBatch;
    private readonly bool _enableDoubleWriteCheck;
    private readonly StorageTree _tree;
    private readonly Hash256 _addressHash;
    private readonly SnapUpperBoundAdapter _adapter;
    private IReadOnlyList<PathWithStorageSlot>? _pendingEntries;

    public FlatSnapStorageTree(IPersistence.IPersistenceReader reader, IPersistence.IWriteBatch writeBatch, Hash256 addressHash, bool enableDoubleWriteCheck, ILogManager logManager)
    {
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
        byte[]? rlp = _reader.TryLoadStorageRlp(_addressHash, path, ReadFlags.None);
        return rlp is not null && ValueKeccak.Compute(rlp) == keccak;
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
                        SlotValue existing = default;
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
    }

    public void Dispose()
    {
        _writeBatch.Dispose();
        _reader.Dispose();
    }
}
