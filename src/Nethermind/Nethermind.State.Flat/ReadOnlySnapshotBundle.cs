// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Utils;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using Prometheus;

namespace Nethermind.State.Flat;

/// <summary>
/// A bundle of <see cref="Snapshot"/> and a layer of write buffer backed by a <see cref="SnapshotContent"/>.
/// </summary>
public sealed class ReadOnlySnapshotBundle : RefCountingDisposable
{
    public int SnapshotCount => _snapshots.Count;

    internal ArrayPoolList<Snapshot> _snapshots;
    private readonly IPersistence.IPersistenceReader _persistenceReader;
    private bool _isDisposed;

    private static Counter _snapshotBundleEvents = DevMetric.Factory.CreateCounter("snapshot_bundle_evens", "event", "type");
    private Counter.Child _nodeGetSnapshots = null!;
    private Counter.Child _nodeGetMiss = null!;
    private Counter.Child _nodeGetSelfDestruct = null!;

    private static Histogram _snapshotBundleTimes = DevMetric.Factory.CreateHistogram("readonly_snapshot_bundle_times", "aha", new HistogramConfiguration()
    {
        LabelNames = new[] { "type" },
        Buckets = Histogram.PowersOfTenDividedBuckets(1, 12, 5)
    });
    private Histogram.Child _accountPersistenceRead = null!;
    private Histogram.Child _slotPersistenceRead = null!;
    private Histogram.Child _accountPersistenceEmptyRead = null!;
    private Histogram.Child _slotPersistenceEmptyRead = null!;
    private Histogram.Child _loadRlpRead = null!;
    private Histogram.Child _loadRlpReadTrieWarmer = null!;
    private Histogram.Child _loadStorageRlpRead = null!;
    private Histogram.Child _loadStorageRlpReadTrieWarmer = null!;

    private Counter.Child _accountGet = null!;
    private Counter.Child _slotGet = null!;

    private Histogram.Child _snapshotAccountHit = null!;
    private Histogram.Child _snapshotAccountMiss = null!;
    private Histogram.Child _snapshotStorageHit = null!;
    private Histogram.Child _snapshotStorageMiss = null!;
    private Histogram.Child _snapshotStorageSelfDestructIdx = null!;

    public ReadOnlySnapshotBundle(ArrayPoolList<Snapshot> snapshots, IPersistence.IPersistenceReader persistenceReader)
    {
        _snapshots = snapshots;
        _persistenceReader = persistenceReader;

        SetupMetric();
    }

    private void SetupMetric()
    {
        _nodeGetSnapshots = _snapshotBundleEvents.WithLabels("node_get_snapshots");
        _nodeGetSelfDestruct = _snapshotBundleEvents.WithLabels("node_get_self_destruct");
        _nodeGetMiss = _snapshotBundleEvents.WithLabels("node_get_miss");
        _accountGet = _snapshotBundleEvents.WithLabels("account_get");
        _slotGet = _snapshotBundleEvents.WithLabels("slot_get");

        _accountPersistenceRead = _snapshotBundleTimes.WithLabels("account_persistence");
        _slotPersistenceRead = _snapshotBundleTimes.WithLabels("slot_persistence");
        _accountPersistenceEmptyRead = _snapshotBundleTimes.WithLabels("empty_account_persistence");
        _slotPersistenceEmptyRead = _snapshotBundleTimes.WithLabels("empty_slot_persistence");
        _loadRlpRead = _snapshotBundleTimes.WithLabels("rlp_read");
        _loadRlpReadTrieWarmer = _snapshotBundleTimes.WithLabels("rlp_read_trie_warmer");
        _loadStorageRlpRead = _snapshotBundleTimes.WithLabels("storage_rlp_read");
        _loadStorageRlpReadTrieWarmer = _snapshotBundleTimes.WithLabels("storage_rlp_read_trie_warmer");

        _snapshotAccountHit = _snapshotBundleTimes.WithLabels("snapshot_account_hit");
        _snapshotAccountMiss = _snapshotBundleTimes.WithLabels("snapshot_account_miss");
        _snapshotStorageHit = _snapshotBundleTimes.WithLabels("snapshot_storage_hit");
        _snapshotStorageMiss = _snapshotBundleTimes.WithLabels("snapshot_storage_miss");
        _snapshotStorageSelfDestructIdx = _snapshotBundleTimes.WithLabels("snapshot_storage_selfdestruct_idx");
    }

    public bool TryGetAccount(Address address, out Account? acc)
    {
        GuardDispose();

        _accountGet.Inc();

        AddressAsKey key = address;

        long sw = Stopwatch.GetTimestamp();
        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (_snapshots[i].TryGetAccount(key, out acc))
            {
                _snapshotAccountHit.Observe(Stopwatch.GetTimestamp() - sw);
                return true;
            }
        }
        _snapshotAccountMiss.Observe(Stopwatch.GetTimestamp() - sw);

        sw = Stopwatch.GetTimestamp();
        acc = _persistenceReader.GetAccount(address);
        if (acc is null)
        {
            _accountPersistenceEmptyRead.Observe(Stopwatch.GetTimestamp() - sw);
        }
        else
        {
            _accountPersistenceRead.Observe(Stopwatch.GetTimestamp() - sw);
        }
        return true;
    }

    public int DetermineSelfDestructSnapshotIdx(Address address)
    {
        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (_snapshots[i].HasSelfDestruct(address))
            {
                return i;
            }
        }

        return -1;
    }

    public bool TryGetSlot(Address address, in UInt256 index, int selfDestructStateIdx, out byte[]? value)
    {
        GuardDispose();

        _slotGet.Inc();

        long sw = Stopwatch.GetTimestamp();
        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (_snapshots[i].TryGetStorage(address, index, out SlotValue? slotValue))
            {
                _snapshotStorageHit.Observe(Stopwatch.GetTimestamp() - sw);
                if (slotValue is null)
                {
                    value = null;
                }
                else
                {
                    value = slotValue.Value.ToEvmBytes();
                }
                return true;
            }

            if (i <= selfDestructStateIdx)
            {
                _snapshotStorageSelfDestructIdx.Observe(Stopwatch.GetTimestamp() - sw);
                value = null;
                return true;
            }
        }
        _snapshotStorageMiss.Observe(Stopwatch.GetTimestamp() - sw);

        sw = Stopwatch.GetTimestamp();

        SlotValue outSlotValue = new SlotValue();

        bool _ = _persistenceReader.TryGetSlot(address, index, ref outSlotValue);
        value = outSlotValue.ToEvmBytes();

        if (value is null || value.Length == 0 || Bytes.AreEqual(value, StorageTree.ZeroBytes))
        {
            _slotPersistenceEmptyRead.Observe(Stopwatch.GetTimestamp() - sw);
        }
        else
        {
            _slotPersistenceRead.Observe(Stopwatch.GetTimestamp() - sw);
        }
        return true;
    }

    public bool TryFindStateNodes(in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
    {
        GuardDispose();

        for (int i = _snapshots.Count - 1; i >= 0; i--)
        {
            if (_snapshots[i].TryGetStateNode(path, out node))
            {
                Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                _nodeGetSnapshots.Inc();
                return true;
            }
        }

        _nodeGetMiss.Inc();
        node = null;
        return false;
    }

    public bool TryFindStorageNodes(Hash256AsKey address, in TreePath path, Hash256 hash, int selfDestructStateIdx, [NotNullWhen(true)] out TrieNode? node)
    {
        for (int i = _snapshots.Count - 1; i >= 0 && i >= selfDestructStateIdx; i--)
        {
            if (_snapshots[i].TryGetStorageNode(address, path, out node))
            {
                Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                _nodeGetSnapshots.Inc();
                return true;
            }
        }

        if (selfDestructStateIdx != -1)
        {
            // If there is a self destruct, there is no need to check further, return true
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            _nodeGetSelfDestruct.Inc();
            node = null;
            return false;
        }

        _nodeGetMiss.Inc();
        node = null;
        return false;
    }

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags, bool isTrieWarmer)
    {
        GuardDispose();

        Nethermind.Trie.Pruning.Metrics.LoadedFromDbNodesCount++;
        long sw = Stopwatch.GetTimestamp();
        var res = _persistenceReader.TryLoadRlp(address, path, flags);
        if (isTrieWarmer)
        {
            if (address is null)
            {
                _loadRlpReadTrieWarmer.Observe(Stopwatch.GetTimestamp() - sw);
            }
            else
            {
                _loadStorageRlpReadTrieWarmer.Observe(Stopwatch.GetTimestamp() - sw);
            }
        }
        else
        {
            if (address is null)
            {
                _loadRlpRead.Observe(Stopwatch.GetTimestamp() - sw);
            }
            else
            {
                _loadStorageRlpRead.Observe(Stopwatch.GetTimestamp() - sw);
            }
        }
        return res;
    }

    private void GuardDispose()
    {
        if (_isDisposed) throw new ObjectDisposedException($"{nameof(ReadOnlySnapshotBundle)} is disposed");
    }

    public bool TryLease()
    {
        return base.TryAcquireLease();
    }

    protected override void CleanUp()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, true, false)) return;

        foreach (Snapshot snapshot in _snapshots)
        {
            snapshot.Dispose();
        }

        // Null them in case unexpected mutation from trie warmer
        _persistenceReader.Dispose();
    }
}
