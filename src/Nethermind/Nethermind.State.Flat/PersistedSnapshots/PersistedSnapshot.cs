// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Int256;
using Nethermind.State.Flat.Storage;
using Nethermind.Trie;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// A persisted snapshot backed by columnar HSST data on disk (or in memory).
/// The outer HSST has 7 column entries, each containing an inner HSST.
/// Inner HSST keys are the entity keys without the tag prefix:
///   Column 0x00: Metadata — String key → version, block range, state root values
///   Column 0x01: Address (20 bytes) → per-address HSST {
///       0x01 (SlotSubTag):         nested HSST (SlotPrefix(30) → nested(SlotSuffix(2) → SlotValue))
///       0x02 (SelfDestructSubTag): raw SD flag bytes (empty = destructed, 0x01 = new account)
///       0x03 (AccountSubTag):      raw account slim RLP bytes (empty = deleted account)
///   }
///   Column 0x03: TreePath (8 bytes compact) → State trie node Keccak hash (path length 6-15)
///   Column 0x05: TreePath (3 bytes: PathByte0, PathByte1, Length) → State trie node Keccak hash (path length 0-5)
///   Column 0x06: TreePath.Path (32 bytes) + PathLength (1 byte) → State trie node Keccak hash (path length 16+)
///   Column 0x07: AddressHash (20 bytes) → nested HSST (TreePath (8 bytes compact) → Storage trie node Keccak hash, path length 6-15)
///   Column 0x08: AddressHash (20 bytes) → nested HSST (TreePath.Path (33 bytes) → Storage trie node Keccak hash, path length 16+)
///
/// Forest-spilled snapshots (block-range span > CompactSize) omit the trie columns entirely.
/// </summary>
public sealed class PersistedSnapshot : RefCountingDisposable
{
    // Tag prefixes for outer HSST columns
    internal static readonly byte[] MetadataTag = [0x00];
    internal static readonly byte[] AccountColumnTag = [0x01];
    internal static readonly byte[] StateNodeTag = [0x03];
    internal static readonly byte[] StateTopNodesTag = [0x05];
    internal static readonly byte[] StateNodeFallbackTag = [0x06];
    internal static readonly byte[] StorageNodeTag = [0x07];
    internal static readonly byte[] StorageNodeFallbackTag = [0x08];

    // Sub-tags within per-address HSST (sorted order)
    internal static readonly byte[] SlotSubTag = [0x01];
    internal static readonly byte[] SelfDestructSubTag = [0x02];
    internal static readonly byte[] AccountSubTag = [0x03];

    private readonly ArenaReservation _reservation;

    /// <summary>
    /// True when this snapshot's trie-node columns were omitted because block-range span exceeds CompactSize.
    /// <see cref="TryLoadStateNodeHash"/> and <see cref="TryLoadStorageNodeHash"/> always return false for such snapshots.
    /// </summary>
    public bool IsForestSpilled { get; }

    public int Id { get; }
    public StateId From { get; }
    public StateId To { get; }
    public PersistedSnapshotType Type { get; }

    public int Size => _reservation.Size;

    public ReadOnlySpan<byte> GetSpan() => _reservation.GetSpan();

    public PersistedSnapshot(int id, StateId from, StateId to, PersistedSnapshotType type, ArenaReservation reservation)
    {
        Id = id;
        From = from;
        To = to;
        Type = type;
        _reservation = reservation;
        _reservation.AcquireLease();
        IsForestSpilled = PersistedSnapshotReader.CheckForestSpilledFlag(GetSpan());
    }

    public bool TryGetAccount(Address address, [UnscopedRef] out ReadOnlySpan<byte> accountRlp) =>
        PersistedSnapshotReader.TryGetAccount(GetSpan(), address, out accountRlp);

    public bool TryGetSlot(Address address, in UInt256 index, [UnscopedRef] out ReadOnlySpan<byte> slotValue) =>
        PersistedSnapshotReader.TryGetSlot(GetSpan(), address, in index, out slotValue);

    public bool IsSelfDestructed(Address address) =>
        PersistedSnapshotReader.IsSelfDestructed(GetSpan(), address);

    /// <summary>
    /// Get the self-destruct flag with boolean distinction.
    /// Returns null if no self-destruct entry exists for this address.
    /// Returns true if this is a new account (value = 0x01), false if destructed (value = empty).
    /// </summary>
    public bool? TryGetSelfDestructFlag(Address address) =>
        PersistedSnapshotReader.TryGetSelfDestructFlag(GetSpan(), address);

    /// <summary>Returns the 32-byte Keccak hash stored for the given state trie path, or false if not present or forest-spilled.</summary>
    public bool TryLoadStateNodeHash(scoped in TreePath path, out ValueHash256 hash)
    {
        if (IsForestSpilled) { hash = default; return false; }
        return PersistedSnapshotReader.TryLoadStateNodeHash(GetSpan(), in path, out hash);
    }

    /// <summary>Returns the 32-byte Keccak hash stored for the given storage trie path, or false if not present or forest-spilled.</summary>
    public bool TryLoadStorageNodeHash(Hash256 address, in TreePath path, out ValueHash256 hash)
    {
        if (IsForestSpilled) { hash = default; return false; }
        return PersistedSnapshotReader.TryLoadStorageNodeHash(GetSpan(), address, in path, out hash);
    }

    // --- Snapshot-matching enumerable properties ---

    public PersistedSnapshotReader.SelfDestructEnumerable SelfDestructedStorageAddresses => new(GetSpan());
    public PersistedSnapshotReader.AccountEnumerable Accounts => new(GetSpan());
    public PersistedSnapshotReader.StorageEnumerable Storages => new(GetSpan());
    public PersistedSnapshotReader.StateNodeEnumerable StateNodes => new(this);
    public PersistedSnapshotReader.StorageNodeEnumerable StorageNodes => new(this);

    public void AdviseDontNeed() => _reservation.AdviseDontNeed();

    public bool TryAcquire() => TryAcquireLease();

    protected override void CleanUp() => _reservation.Dispose();
}
