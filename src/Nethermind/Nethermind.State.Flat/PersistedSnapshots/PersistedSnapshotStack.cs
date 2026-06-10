// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// The persisted-snapshot half of a <see cref="ReadOnlySnapshotBundle"/>: a stack of
/// <see cref="PersistedSnapshot"/>s probed newest-first, each gated by the
/// <see cref="PersistedSnapshotBloom"/> leased for it before any disk read is paid.
/// </summary>
/// <remarks>
/// Owns both the snapshot list and the parallel bloom list (one leased bloom per snapshot,
/// same index) — <see cref="Dispose"/> releases them in lock-step. Also owns the detailed
/// metrics recorded around the probe loops: each <c>*_persisted_snapshot</c> hit label and
/// the per-key-kind skip-time observations.
/// </remarks>
public sealed class PersistedSnapshotStack : IDisposable
{
    private static readonly StringLabel _readAccountPersistedLabel = new("account_persisted_snapshot");
    private static readonly StringLabel _readStoragePersistedLabel = new("storage_persisted_snapshot");
    private static readonly StringLabel _readStateRlpPersistedLabel = new("state_rlp_persisted_snapshot");
    private static readonly StringLabel _readStorageRlpPersistedLabel = new("storage_rlp_persisted_snapshot");

    private static readonly StringLabel _skipAccountLabel = new("account");
    private static readonly StringLabel _skipSlotLabel = new("slot");
    private static readonly StringLabel _skipStateRlpLabel = new("state_rlp");
    private static readonly StringLabel _skipStorageRlpLabel = new("storage_rlp");

    private readonly PersistedSnapshotList _snapshots;
    private readonly ArrayPoolList<PersistedSnapshotBloom> _blooms;
    private readonly bool _recordDetailedMetrics;

    public PersistedSnapshotStack(
        PersistedSnapshotList snapshots,
        ArrayPoolList<PersistedSnapshotBloom> blooms,
        bool recordDetailedMetrics)
    {
        Debug.Assert(snapshots.Count == blooms.Count, "One leased bloom per persisted snapshot");
        _snapshots = snapshots;
        _blooms = blooms;
        _recordDetailedMetrics = recordDetailedMetrics;
    }

    public static PersistedSnapshotStack Empty(bool recordDetailedMetrics = false) =>
        new(PersistedSnapshotList.Empty(), new ArrayPoolList<PersistedSnapshotBloom>(0), recordDetailedMetrics);

    public int Count => _snapshots.Count;

    /// <summary>
    /// Probe the stack newest-first for the account at <paramref name="address"/>.
    /// </summary>
    /// <returns><c>true</c> when a snapshot holds an entry for the address —
    /// <paramref name="account"/> is then the stored account, or <c>null</c> for a
    /// deletion marker. <c>false</c> means the caller should fall through to persistence.</returns>
    public bool TryGetAccount(Address address, out Account? account)
    {
        // PersistedSnapshot's per-address column is keyed by raw Address; the bloom seed
        // also derives from raw Address bytes, so no Keccak round-trip is needed here.
        long psw = _recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        if (_snapshots.Count > 0)
        {
            ulong addrBloomKey = PersistedSnapshotBloomBuilder.AddressKey(address);
            for (int i = _snapshots.Count - 1; i >= 0; i--)
            {
                if (!_blooms[i].Bloom.MightContain(addrBloomKey)) continue;
                if (_snapshots[i].TryGetAccount(address, out account))
                {
                    if (_recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - psw, _readAccountPersistedLabel);
                    return true;
                }
            }
        }
        if (_recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleSkipTime.Observe(Stopwatch.GetTimestamp() - psw, _skipAccountLabel);

        account = null;
        return false;
    }

    /// <summary>
    /// Find the index (within this stack) of the newest snapshot carrying a self-destruct
    /// flag for <paramref name="address"/>.
    /// </summary>
    public bool TryGetSelfDestruct(Address address, out int snapshotIdx)
    {
        if (_snapshots.Count > 0)
        {
            ulong addrBloomKey = PersistedSnapshotBloomBuilder.AddressKey(address);
            for (int i = _snapshots.Count - 1; i >= 0; i--)
            {
                if (!_blooms[i].Bloom.MightContain(addrBloomKey)) continue;
                bool? flag = _snapshots[i].TryGetSelfDestructFlag(address);
                if (flag.HasValue)
                {
                    snapshotIdx = i;
                    return true;
                }
            }
        }

        snapshotIdx = -1;
        return false;
    }

    /// <summary>
    /// Probe the stack newest-first for the storage slot, stopping at the self-destruct
    /// boundary.
    /// </summary>
    /// <param name="selfDestructStateIdx">Index (within this stack) of the snapshot holding
    /// the newest self-destruct for the address; snapshots at or below it are not probed.</param>
    /// <param name="lookupStart">Timestamp of the bundle-level lookup start; the hit
    /// observation is based here so the recorded time spans the in-memory scan too,
    /// matching the label's historical semantics.</param>
    /// <returns><c>true</c> when the stack resolved the slot definitively — either a stored
    /// value, or <c>null</c> because the self-destruct boundary was reached. <c>false</c>
    /// means the caller should fall through to persistence.</returns>
    public bool TryGetSlot(Address address, in UInt256 index, int selfDestructStateIdx, long lookupStart, out byte[]? value)
    {
        long psw = _recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        // Bloom checks both the address-key and the per-slot key before paying for a
        // column seek into the persisted snapshot. PersistedSnapshot's per-address column
        // is keyed by raw Address; the bloom seed derives from raw Address bytes directly.
        if (_snapshots.Count > 0)
        {
            ulong addrBloomKey = PersistedSnapshotBloomBuilder.AddressKey(address);
            ulong slotBloomKey = PersistedSnapshotBloomBuilder.SlotKey(addrBloomKey, in index);
            for (int i = _snapshots.Count - 1; i >= 0; i--)
            {
                PersistedSnapshotBloom bloom = _blooms[i];
                if (bloom.Bloom.MightContain(addrBloomKey) && bloom.Bloom.MightContain(slotBloomKey))
                {
                    SlotValue slotValue = default;
                    if (_snapshots[i].TryGetSlot(address, in index, ref slotValue))
                    {
                        if (_recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - lookupStart, _readStoragePersistedLabel);
                        value = slotValue.ToEvmBytes();
                        return true;
                    }
                }

                if (i <= selfDestructStateIdx)
                {
                    value = null;
                    return true;
                }
            }
        }
        if (_recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleSkipTime.Observe(Stopwatch.GetTimestamp() - psw, _skipSlotLabel);

        value = null;
        return false;
    }

    /// <summary>
    /// Probe the stack newest-first for the state-trie node RLP at <paramref name="path"/>.
    /// </summary>
    public bool TryLoadStateRlp(in TreePath path, out byte[]? rlp)
    {
        long sw = _recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        ulong statePathBloomKey = PersistedSnapshotBloomBuilder.StatePathKey(in path);
        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (!_blooms[i].Bloom.MightContain(statePathBloomKey)) continue;
            if (_snapshots[i].TryLoadStateNodeRlp(in path, out rlp))
            {
                if (_recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStateRlpPersistedLabel);
                return true;
            }
        }
        if (_recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleSkipTime.Observe(Stopwatch.GetTimestamp() - sw, _skipStateRlpLabel);

        rlp = null;
        return false;
    }

    /// <summary>
    /// Probe the stack newest-first for the storage-trie node RLP at
    /// (<paramref name="address"/>, <paramref name="path"/>).
    /// </summary>
    public bool TryLoadStorageRlp(Hash256 address, in TreePath path, out byte[]? rlp)
    {
        long sw = _recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        // Caller already provides the address-hash; convert to the struct ValueHash256
        // (no alloc) so the read path stays Hash256-free below.
        ValueHash256 addressHash = address.ValueHash256;
        ulong storageBloomKey = PersistedSnapshotBloomBuilder.StorageNodeKey(in addressHash, in path);
        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (!_blooms[i].Bloom.MightContain(storageBloomKey)) continue;
            if (_snapshots[i].TryLoadStorageNodeRlp(in addressHash, in path, out rlp))
            {
                if (_recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - sw, _readStorageRlpPersistedLabel);
                return true;
            }
        }
        if (_recordDetailedMetrics) Metrics.ReadOnlySnapshotBundleSkipTime.Observe(Stopwatch.GetTimestamp() - sw, _skipStorageRlpLabel);

        rlp = null;
        return false;
    }

    public void Dispose()
    {
        _snapshots.Dispose();
        for (int i = 0; i < _blooms.Count; i++)
            _blooms[i].Dispose();
        _blooms.Dispose();
    }
}
