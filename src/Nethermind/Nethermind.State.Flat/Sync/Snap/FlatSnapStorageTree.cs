// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Sync.Snap;

/// <summary>
/// ISnapTree adapter for flat snap sync (storage).
/// Owns reader (for IsPersisted) and writeBatch (for commits), disposing them on Dispose.
/// </summary>
public class FlatSnapStorageTree(IPersistence.IPersistenceReader reader, IPersistence.IWriteBatch writeBatch, Hash256 addressHash, bool enableDoubleWriteCheck) : ISnapTree<PathWithStorageSlot>
{
    private readonly SnapNodeRecordTrie _trie = new(reader, writeBatch, addressHash, enableDoubleWriteCheck);
    private IReadOnlyList<PathWithStorageSlot>? _pendingEntries;

    public Hash256 RootHash => _trie.RootHash;

    public void SetRootFromProof(TrieNode root) => _trie.SetRootFromProof(root);

    public bool IsPersisted(in TreePath path, in ValueHash256 keccak) => _trie.IsPersisted(path, keccak);

    public void BulkSetAndUpdateRootHash(IReadOnlyList<PathWithStorageSlot> entries)
    {
        _pendingEntries = entries;
        _trie.BulkSet(entries);
    }

    public void Commit(ValueHash256 upperBound)
    {
        if (_pendingEntries is not null)
        {
            for (int i = 0; i < _pendingEntries.Count; i++)
            {
                PathWithStorageSlot slot = _pendingEntries[i];
                if (slot.Path <= upperBound)
                {
                    if (enableDoubleWriteCheck)
                    {
                        UInt256 existing = default;
                        if (reader.TryGetStorageRaw(addressHash, slot.Path, ref existing))
                            throw new Exception($"Double storage flat write. address:{addressHash} slot:{slot.Path} firstEntry:{_pendingEntries[0].Path} lastEntry:{_pendingEntries[_pendingEntries.Count - 1].Path} upperBound:{upperBound}");
                    }
                    // slot.SlotRlpValue is already RLP(stripped) — the on-disk format when wrapping is on,
                    // so this avoids a decode + re-encode round-trip.
                    writeBatch.SetStorageRawEncoded(addressHash, slot.Path, slot.SlotRlpValue);
                }
            }
        }

        _trie.Commit(upperBound, skipRoot: false);
    }

    public void Dispose()
    {
        _trie.Dispose();
        writeBatch.Dispose();
        reader.Dispose();
    }
}
