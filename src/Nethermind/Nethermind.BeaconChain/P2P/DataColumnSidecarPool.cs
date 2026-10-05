// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.SszRest;

namespace Nethermind.BeaconChain.P2P;

/// <summary>Holds verified Fulu and Gloas sidecars for by-range and by-root serving.</summary>
/// <remarks>
/// With a store, additions persist and cache misses read through; pruning runs once per wall-clock epoch. Without one,
/// retention is memory-only. Each fork's served map evicts lowest slots first, retaining a complete suffix plus capacity
/// recent arrivals so range-synced columns remain available to import. Keys are (block root, column), not slot,
/// so by-range serving resolves the canonical root first. Pending Gloas candidates are never served before AddGloas verifies them.
/// Store retention must satisfy Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests.
/// </remarks>
/// <param name="capacity">Maximum sidecars per served map.</param>
/// <param name="store">Persistence and read-through storage; null is memory-only.</param>
/// <param name="clock">Retention clock; null disables store pruning.</param>
/// <param name="status">Finalized epoch for pruning noncanonical sidecars; null disables that pruning.</param>
/// <param name="logManager">Reports store faults.</param>
/// <param name="storeWriter">Runs writes and prunes in order after memory insertion; null runs on the caller.</param>
public sealed class DataColumnSidecarPool(int capacity = 1 << 14, BeaconChainStore? store = null, SlotClock? clock = null, IBeaconChainStatusSource? status = null, ILogManager? logManager = null,
    ColumnStoreWriter? storeWriter = null)
{
    private readonly ILogger _logger = (logManager ?? NullLogManager.Instance).GetClassLogger<DataColumnSidecarPool>();

    private readonly Lock _servedLock = new();
    private readonly SlotOrderedSidecars<DataColumnSidecar> _byRootAndColumn = new(capacity);
    private readonly SlotOrderedSidecars<DataColumnSidecarGloas> _gloasByRootAndColumn = new(capacity);
    private ulong? _firstGivenSlot;
    private ulong _seededFloor;
    private ulong _writeFailedBelow;
    private long _writeFailureVersion;
    private ulong _writeFailedPersisted;
    private ulong _storedRangeUncheckedBelow;
    private ulong? _backfilledFrom;
    private ulong _prunedFloor;
    private int _storeReadFaultReported;
    private ulong? _storedFloor = store is not null && store.TryGetDataColumnFloor(out ulong storedFloor) ? storedFloor : null;
    private long _lastPrunedEpoch = -1;

    // A corrupt record would otherwise be read and decoded again for every request that names it.
    private readonly LruKeyCache<(Hash256 BlockRoot, ulong Column)> _unreadable = new(256, "unreadable data column sidecars");
    // A sidecar whose store write is queued stays readable here until the store holds it, as memory may evict it first.
    private readonly ConcurrentDictionary<(Hash256 BlockRoot, ulong Column), object> _unwritten = new();
    // Pending sidecars are unverified and peer-supplied, so they get a smaller bound than the served maps.
    private readonly int _maxPendingGloas = Math.Min(capacity, MaxPendingGloasSidecars);
    private readonly Lock _pendingLock = new();
    private readonly Dictionary<(Hash256 BlockRoot, ulong Column), List<DataColumnSidecarGloas>> _pendingByKey = [];
    private int _pendingCount;
    private ulong _pendingPrunedAtSlot;
    private readonly Dictionary<Hash256, ColumnWatch> _watches = [];
    private int _watchCount;

    /// <summary>The most unverified Gloas sidecars <see cref="AddPendingGloas"/> holds at once.</summary>
    public const int MaxPendingGloasSidecars = 1 << 10;

    /// <summary>The most unverified Gloas sidecars <see cref="AddPendingGloas"/> holds for one (root, column).</summary>
    /// <remarks>Availability runs a KZG batch per candidate, so this bounds that work per column; above one, so an earlier forgery cannot block the genuine sidecar alone.</remarks>
    public const int MaxPendingGloasCandidatesPerKey = 4;

    internal int SlotIndexCount
    {
        get
        {
            lock (_servedLock)
            {
                return _byRootAndColumn.SlotCount + _gloasByRootAndColumn.SlotCount;
            }
        }
    }

    /// <summary>
    /// The lowest slot from which every sidecar this pool was given is still retained; <see cref="ulong.MaxValue"/> before the first one.
    /// </summary>
    /// <remarks>
    /// A lower slot either lost or was refused a sidecar, or is below the slot of the first sidecar this process received,
    /// so its columns cannot be assumed complete. The start-up floor is released after the stored range is checked; verified backfill can lower it (fulu/p2p-interface.md).
    /// A block that carried no blobs has no sidecars, so its
    /// slot never counts against this. With a store, memory eviction loses nothing, so the floor is the one the store recorded
    /// when the first sidecar was given, raised by pruning and by a failed write, and it survives a restart.
    /// </remarks>
    internal ulong EarliestCompletelyServableSlot
    {
        get
        {
            lock (_servedLock)
            {
                ulong held = store is null
                    ? Math.Max(_firstGivenSlot ?? ulong.MaxValue, Math.Max(_byRootAndColumn.IncompleteBelow, _gloasByRootAndColumn.IncompleteBelow))
                    : _storedFloor ?? ulong.MaxValue;
                ulong floor = Math.Max(held, Math.Max(_seededFloor, _writeFailedBelow));
                ulong completed = _backfilledFrom is { } backfilled ? Math.Min(backfilled, floor) : floor;
                ulong retained = store is null ? Math.Max(_byRootAndColumn.IncompleteBelow, _gloasByRootAndColumn.IncompleteBelow)
                    : _prunedFloor;
                return Math.Max(completed, Math.Max(retained, Math.Max(_writeFailedBelow, _storedRangeUncheckedBelow)));
            }
        }
    }

    internal long WriteFailureVersion
    {
        get { lock (_servedLock) return _writeFailureVersion; }
    }

    // Only a verified range can clear old write failures; a concurrent failure retains its floor (fulu/p2p-interface.md).
    internal void LowerCompletelyServableFloor(ulong slot, ulong? verifiedThrough = null, long? failureVersion = null)
    {
        lock (_servedLock)
        {
            _backfilledFrom = _backfilledFrom is { } known ? Math.Min(known, slot) : slot;
            if (failureVersion == _writeFailureVersion && verifiedThrough is { } through
                && _writeFailedBelow > slot && _writeFailedBelow - 1 <= through)
            {
                _writeFailedBelow = 0;
            }
        }
    }

    /// <summary>
    /// Floors <see cref="EarliestCompletelyServableSlot"/> one past the canonical index top at start-up, unless the store already recorded a floor;
    /// A recorded floor is checked in the background and stays above the top until complete (fulu/p2p-interface.md DataColumnSidecarsByRange).
    /// </summary>
    /// <param name="canonicalIndexTopSlot">The highest slot the canonical index may hold, or <c>null</c> for a database that has none.</param>
    /// <remarks>
    /// The first sidecar this process is given can sit below the head it resumes from, and would otherwise make every slot up to the head look
    /// complete. A recorded store floor already says which slots the store holds, so it is not overridden, only checked: a write the store
    /// refused together with the floor it would have raised leaves a slot incomplete that the recorded floor still covers.
    /// </remarks>
    /// <param name="cancellationToken">Stops the stored range check, which then leaves the floor at its conservative value.</param>
    /// <returns>The stored range check, which runs off the caller's thread; already complete when there is none. It never faults.</returns>
    internal Task SeedCompletelyServableFloor(ulong? canonicalIndexTopSlot, CancellationToken cancellationToken = default)
    {
        if (canonicalIndexTopSlot is not { } top)
        {
            return Task.CompletedTask;
        }

        ulong storedFloor;
        lock (_servedLock)
        {
            if (_storedFloor is null)
            {
                _seededFloor = Math.Max(_seededFloor, top == ulong.MaxValue ? top : top + 1);
                return Task.CompletedTask;
            }

            storedFloor = _storedFloor.Value;
            _storedRangeUncheckedBelow = top == ulong.MaxValue ? top : top + 1;
        }

        return Task.Run(() => RaiseFloorAboveIncompleteStoredSlot(storedFloor, top, cancellationToken), CancellationToken.None);
    }

    /// <summary>Checks the stored range and lifts the floor held back for it; a check that cannot finish leaves that floor in place, so nothing below the top is claimed complete unchecked.</summary>
    private void RaiseFloorAboveIncompleteStoredSlot(ulong storedFloor, ulong top, CancellationToken cancellationToken)
    {
        ulong? incomplete;
        BeaconChainStore.DataColumnShortfall shortfall;
        try
        {
            incomplete = store!.FindIncompleteDataColumnSlot(storedFloor, top, clock?.CurrentEpoch, StoredSampledColumns(), out shortfall, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (_logger.IsError) _logger.Error($"Could not check the stored data column sidecars ({e.GetType().Name.Replace(nameof(Exception), string.Empty, StringComparison.Ordinal)}); DataColumnSidecarsByRange is served as complete only above slot {top}");
            return;
        }

        ulong servedFrom = incomplete is { } slot ? (slot == ulong.MaxValue ? slot : slot + 1) : 0;
        lock (_servedLock)
        {
            _writeFailedBelow = Math.Max(_writeFailedBelow, servedFrom);
            _storedRangeUncheckedBelow = 0;
        }

        if (incomplete is { } incompleteSlot)
        {
            if (_logger.IsInfo) _logger.Info($"Stored data column sidecars at slot {incompleteSlot} are incomplete ({shortfall}); DataColumnSidecarsByRange is served as complete from slot {servedFrom}");
            PersistWriteFailedFloor();
        }
    }

    // Discovery derives the node id from this stored key with CUSTODY_REQUIREMENT groups; import needed every sampled column, which includes the custody ones (fulu/das-core.md).
    private UInt128? StoredSampledColumns()
    {
        try
        {
            if (store!.GetMetadata(BeaconDiscovery.IdentityMetadataKey) is not { } key)
            {
                return null;
            }

            using PrivateKey nodeKey = new(key);
            UInt128 columns = UInt128.Zero;
            foreach (ulong column in new NodeColumnCustody(nodeKey.PublicKey.Hash, Eip7594DasConstants.CustodyRequirement).SampledColumns)
            {
                columns |= UInt128.One << (int)column;
            }

            return columns;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Collects the Fulu sidecars held for <paramref name="blockRoot"/> at <paramref name="slot"/>, memory and store together, one per column, once there are at least <paramref name="atLeast"/>.</summary>
    /// <remarks>The store is asked for its column bitmap only when memory alone falls short, and reads only the columns it holds, so a block whose columns arrived before a restart still reconstructs.</remarks>
    /// <returns><c>false</c> when fewer are held.</returns>
    internal bool TryGetHeldColumns(Hash256 blockRoot, ulong slot, int atLeast, [NotNullWhen(true)] out DataColumnSidecar[]? held)
    {
        UInt128 columns = UInt128.Zero;
        int inMemory = 0;
        lock (_servedLock)
        {
            for (ulong column = 0; column < Eip7594DasConstants.NumberOfColumns; column++)
            {
                if (_byRootAndColumn.TryGet((blockRoot, column), out _)
                    || (_unwritten.TryGetValue((blockRoot, column), out object? unwritten) && unwritten is DataColumnSidecar))
                {
                    columns |= UInt128.One << (int)column;
                    inMemory++;
                }
            }
        }

        if (inMemory < atLeast && store is not null)
        {
            columns |= ReadStoredColumns(slot, blockRoot);
        }

        int candidates = (int)UInt128.PopCount(columns);
        if (candidates < atLeast)
        {
            held = null;
            return false;
        }

        DataColumnSidecar[] found = new DataColumnSidecar[candidates];
        int next = 0;
        for (ulong column = 0; column < Eip7594DasConstants.NumberOfColumns; column++)
        {
            if ((columns >> (int)column & UInt128.One) != UInt128.Zero && TryGet(blockRoot, column, out DataColumnSidecar? sidecar) && sidecar is not null)
            {
                found[next++] = sidecar;
            }
        }

        if (next < atLeast)
        {
            held = null;
            return false;
        }

        held = next == found.Length ? found : found[..next];
        return true;
    }

    private UInt128 ReadStoredColumns(ulong slot, Hash256 blockRoot)
    {
        try
        {
            return store!.GetStoredDataColumns(slot, blockRoot);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ReportStoreReadFault(e);
            return UInt128.Zero;
        }
    }

    private bool StoreHolds(Hash256 blockRoot, ulong column)
    {
        if (_unwritten.ContainsKey((blockRoot, column)))
        {
            return true;
        }

        if (_unreadable.Get((blockRoot, column)))
        {
            return false;
        }

        try
        {
            return store!.HasDataColumnRecord(blockRoot, column);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ReportStoreReadFault(e);
            return false;
        }
    }

    // A failing database would otherwise log once per request that names a stored column.
    private void ReportStoreReadFault(Exception e)
    {
        if (Interlocked.Exchange(ref _storeReadFaultReported, 1) == 0 && _logger.IsError)
        {
            _logger.Error($"Reading stored data column sidecars failed; the affected ones are not served until the store recovers: {PeerManager.DescribeFailure(e)}");
        }
    }

    public void Add(Hash256 blockRoot, ulong slot, DataColumnSidecar sidecar)
    {
        Action? wake;
        TrackUnwritten(blockRoot, sidecar.Index, sidecar);
        lock (_servedLock)
        {
            MarkGiven(slot);
            _byRootAndColumn.Set((blockRoot, sidecar.Index), slot, sidecar);
            wake = TakeWakeOnArrival(blockRoot, sidecar.Index);
        }

        QueuePersist(slot, blockRoot, sidecar.Index, sidecar, null);
        wake?.Invoke();
    }

    /// <summary>
    /// Registers <paramref name="wake"/> to run once, on the thread that adds the last of the <paramref name="columns"/> not yet held for
    /// <paramref name="blockRoot"/>, replacing an earlier watch on the root.
    /// </summary>
    /// <param name="gloas">Whether the columns are Gloas sidecars, where a parked candidate counts as held.</param>
    /// <returns>Whether the watch is set; <c>false</c> when every column is held already, so no arrival would wake it.</returns>
    /// <remarks>
    /// A column counts once it is given to the pool, whether or not it later verifies, so the woken retry decides availability.
    /// Only roots watched cost anything per sidecar, and the caller bounds the watches by the blocks it waits for.
    /// </remarks>
    internal bool TryWatch(Hash256 blockRoot, IReadOnlyList<ulong> columns, bool gloas, Action wake)
    {
        // The store is probed before the lock and memory inside it: a column added in between is in memory first, so it is seen either way.
        IReadOnlyList<ulong> notStored = store is null ? columns : ColumnsNotStored(blockRoot, columns);
        HashSet<ulong> missing = [];
        lock (_servedLock)
        {
            foreach (ulong column in notStored)
            {
                if (!(gloas ? _gloasByRootAndColumn.TryGet((blockRoot, column), out _) || HasPendingGloas(blockRoot, column) : _byRootAndColumn.TryGet((blockRoot, column), out _)))
                {
                    missing.Add(column);
                }
            }

            if (missing.Count == 0)
            {
                _watches.Remove(blockRoot);
                Volatile.Write(ref _watchCount, _watches.Count);
                return false;
            }

            _watches[blockRoot] = new ColumnWatch(missing, wake);
            Volatile.Write(ref _watchCount, _watches.Count);
            return true;
        }
    }

    private List<ulong> ColumnsNotStored(Hash256 blockRoot, IReadOnlyList<ulong> columns)
    {
        List<ulong> notStored = new(columns.Count);
        foreach (ulong column in columns)
        {
            if (!StoreHolds(blockRoot, column))
            {
                notStored.Add(column);
            }
        }

        return notStored;
    }

    internal void Unwatch(Hash256 blockRoot)
    {
        lock (_servedLock)
        {
            _watches.Remove(blockRoot);
            Volatile.Write(ref _watchCount, _watches.Count);
        }
    }

    internal int WatchCount
    {
        get
        {
            lock (_servedLock)
            {
                return _watches.Count;
            }
        }
    }

    private Action? TakeWakeOnArrival(Hash256 blockRoot, ulong column)
    {
        if (_watches.Count == 0 || !_watches.TryGetValue(blockRoot, out ColumnWatch? watch) || !watch.Missing.Remove(column) || watch.Missing.Count > 0)
        {
            return null;
        }

        _watches.Remove(blockRoot);
        Volatile.Write(ref _watchCount, _watches.Count);
        return watch.Wake;
    }

    private bool HasPendingGloas(Hash256 blockRoot, ulong column)
    {
        lock (_pendingLock)
        {
            return _pendingByKey.ContainsKey((blockRoot, column));
        }
    }

    private sealed class ColumnWatch(HashSet<ulong> missing, Action wake)
    {
        public HashSet<ulong> Missing { get; } = missing;
        public Action Wake { get; } = wake;
    }

    /// <remarks>A sidecar the memory no longer holds is read from the store without being cached, so a range request never displaces the recent set.</remarks>
    public bool TryGet(Hash256 blockRoot, ulong column, out DataColumnSidecar? sidecar)
    {
        lock (_servedLock)
        {
            if (_byRootAndColumn.TryGet((blockRoot, column), out sidecar))
            {
                return true;
            }
        }

        if (_unwritten.TryGetValue((blockRoot, column), out object? unwritten) && unwritten is DataColumnSidecar queued)
        {
            sidecar = queued;
            return true;
        }

        sidecar = null;
        if (store is null || _unreadable.Get((blockRoot, column)))
        {
            return false;
        }

        try
        {
            bool found = store.TryGetDataColumnSidecar(blockRoot, column, out sidecar);
            Volatile.Write(ref _storeReadFaultReported, 0);
            return found;
        }
        catch (InvalidDataException e)
        {
            MarkUnreadable(blockRoot, column, gloas: false, e);
            return false;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ReportStoreReadFault(e);
            return false;
        }
    }

    /// <summary>Stores a verified Gloas sidecar under its own <c>beacon_block_root</c> and <c>index</c>, and drops every pending candidate for them.</summary>
    /// <exception cref="ArgumentException"><paramref name="sidecar"/> names no beacon block root.</exception>
    public void AddGloas(DataColumnSidecarGloas sidecar)
    {
        Hash256 blockRoot = BlockRootOf(sidecar);
        Action? wake;
        TrackUnwritten(blockRoot, sidecar.Index, sidecar);
        lock (_servedLock)
        {
            MarkGiven(sidecar.Slot);
            _gloasByRootAndColumn.Set((blockRoot, sidecar.Index), sidecar.Slot, sidecar);
            wake = TakeWakeOnArrival(blockRoot, sidecar.Index);
        }

        QueuePersist(sidecar.Slot, blockRoot, sidecar.Index, null, sidecar);

        lock (_pendingLock)
        {
            if (_pendingByKey.Remove((blockRoot, sidecar.Index), out List<DataColumnSidecarGloas>? candidates))
            {
                _pendingCount -= candidates.Count;
            }
        }

        wake?.Invoke();
    }

    public bool TryGetGloas(Hash256 blockRoot, ulong column, [NotNullWhen(true)] out DataColumnSidecarGloas? sidecar)
    {
        lock (_servedLock)
        {
            if (_gloasByRootAndColumn.TryGet((blockRoot, column), out sidecar))
            {
                return true;
            }
        }

        if (_unwritten.TryGetValue((blockRoot, column), out object? unwritten) && unwritten is DataColumnSidecarGloas queued)
        {
            sidecar = queued;
            return true;
        }

        sidecar = null;
        if (store is null || _unreadable.Get((blockRoot, column)))
        {
            return false;
        }

        try
        {
            bool found = store.TryGetDataColumnSidecarGloas(blockRoot, column, out sidecar);
            Volatile.Write(ref _storeReadFaultReported, 0);
            return found;
        }
        catch (InvalidDataException e)
        {
            MarkUnreadable(blockRoot, column, gloas: true, e);
            return false;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ReportStoreReadFault(e);
            return false;
        }
    }

    /// <summary>Reads accepted columns awaiting persistence without taking the store writer's locks.</summary>
    internal SszBlobCell[]? GetUnwrittenCells(Hash256 blockRoot, ulong column, bool gloas) =>
        _unwritten.TryGetValue((blockRoot, column), out object? sidecar) ? sidecar switch
        {
            DataColumnSidecar fulu when !gloas => fulu.Column ?? [],
            DataColumnSidecarGloas value when gloas => value.Column ?? [],
            _ => null,
        } : null;

    private void MarkUnreadable(Hash256 blockRoot, ulong column, bool gloas, InvalidDataException e)
    {
        _unreadable.Set((blockRoot, column));
        // A write that replaced the record during the failed read clears the marker either after this set or before this re-read.
        try
        {
            if (gloas ? store!.TryGetDataColumnSidecarGloas(blockRoot, column, out _) : store!.TryGetDataColumnSidecar(blockRoot, column, out _))
            {
                _unreadable.Delete((blockRoot, column));
                return;
            }
        }
        catch (Exception reread) when (reread is not OutOfMemoryException)
        {
        }

        if (_logger.IsWarn) _logger.Warn($"Stored data column sidecar {blockRoot} index {column} is unreadable and is not served: {e.Message}");
    }

    private void QueuePersist(ulong slot, Hash256 blockRoot, ulong column, DataColumnSidecar? fulu, DataColumnSidecarGloas? gloas)
    {
        if (store is null)
        {
            return;
        }

        if (storeWriter is null)
        {
            Persist(slot, blockRoot, column, fulu, gloas);
        }
        else
        {
            storeWriter.Post(() =>
            {
                try
                {
                    Persist(slot, blockRoot, column, fulu, gloas);
                }
                finally
                {
                    // Only this write's entry: a later copy queued for the same key stays until its own write.
                    _unwritten.TryRemove(new KeyValuePair<(Hash256, ulong), object>((blockRoot, column), (object?)fulu ?? gloas!));
                }
            });
        }
    }

    /// <summary>Completes once every sidecar added before this call is in the store, or its write failed.</summary>
    /// <remarks>A reader of the store itself, such as a check of the columns it holds, must wait for this first.</remarks>
    internal Task WhenStored() => storeWriter?.WhenWritten() ?? Task.CompletedTask;

    // Tracked before memory holds the sidecar and released only after the store does, so a reader that misses memory finds one or the other.
    private void TrackUnwritten(Hash256 blockRoot, ulong column, object sidecar)
    {
        if (storeWriter is not null && store is not null)
        {
            _unwritten[(blockRoot, column)] = sidecar;
        }
    }

    /// <remarks>Failed writes raise the persisted floor where possible, so eviction or restart cannot claim the slot servable.</remarks>
    private void Persist(ulong slot, Hash256 blockRoot, ulong column, DataColumnSidecar? fulu, DataColumnSidecarGloas? gloas)
    {
        if (store is null)
        {
            return;
        }

        try
        {
            if (fulu is not null)
            {
                store.PutDataColumnSidecar(blockRoot, slot, fulu);
            }
            else
            {
                store.PutDataColumnSidecar(gloas!);
            }

            _unreadable.Delete((blockRoot, column));
            FixStoredFloor(slot);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (_logger.IsError) _logger.Error($"Could not store data column sidecar {blockRoot} index {column}; slots up to {slot} are no longer served as complete: {PeerManager.DescribeFailure(e)}");
            lock (_servedLock)
            {
                _writeFailedBelow = Math.Max(_writeFailedBelow, slot == ulong.MaxValue ? slot : slot + 1);
                _writeFailureVersion++;
            }
        }

        PersistWriteFailedFloor();
        PruneOncePerEpoch();
    }

    private void PersistWriteFailedFloor()
    {
        ulong failedBelow;
        ulong floor;
        lock (_servedLock)
        {
            failedBelow = _writeFailedBelow;
            if (failedBelow <= _writeFailedPersisted)
            {
                return;
            }

            floor = Math.Max(failedBelow, _seededFloor);
        }

        try
        {
            store!.RaiseDataColumnFloor(floor);
            lock (_servedLock)
            {
                _writeFailedPersisted = Math.Max(_writeFailedPersisted, failedBelow);
                _storedFloor = _storedFloor is { } known ? Math.Max(known, floor) : floor;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (_logger.IsError) _logger.Error($"Could not record that slots up to {failedBelow} lost a data column sidecar; retried after the next one: {PeerManager.DescribeFailure(e)}");
        }
    }

    // The seed carries into the recorded floor, so a later start, which skips the seed, never takes a late column's slot for it.
    private void FixStoredFloor(ulong slot)
    {
        lock (_servedLock)
        {
            if (_storedFloor is not null)
            {
                return;
            }

            ulong floor = Math.Max(slot, _seededFloor);
            store!.RaiseDataColumnFloor(floor);
            _storedFloor = floor;
        }
    }

    private void PruneOncePerEpoch()
    {
        if (store is null || clock is null)
        {
            return;
        }

        long epoch = (long)clock.CurrentEpoch;
        long previous = Volatile.Read(ref _lastPrunedEpoch);
        if (previous == epoch || Interlocked.CompareExchange(ref _lastPrunedEpoch, epoch, previous) != previous)
        {
            return;
        }

        try
        {
            ulong retainedFrom = store.GetDataColumnRetentionFloor((ulong)epoch);
            lock (_servedLock)
            {
                _prunedFloor = Math.Max(_prunedFloor, retainedFrom);
            }

            store.PruneDataColumnSidecars((ulong)epoch, status is null ? 0 : BeaconStateAccessors.ComputeStartSlotAtEpoch(status.CurrentStatus.FinalizedEpoch));
            if (store.TryGetDataColumnFloor(out ulong floor))
            {
                lock (_servedLock)
                {
                    _storedFloor = _storedFloor is { } known ? Math.Max(known, floor) : _storedFloor;
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (_logger.IsError) _logger.Error($"Pruning the stored data column sidecars failed; they are pruned again next epoch: {PeerManager.DescribeFailure(e)}");
        }
    }

    /// <summary>
    /// Parks an unverified Gloas sidecar whose block is not yet known as a candidate for its own
    /// <c>beacon_block_root</c> and <c>index</c>, unless that key or the pool is full.
    /// </summary>
    /// <param name="sidecar">The unverified sidecar.</param>
    /// <param name="currentSlot">The wall-clock slot, which sets the retention window.</param>
    /// <returns>Whether the sidecar was parked.</returns>
    /// <remarks>
    /// The source of an unverified sidecar is unknown, so no arrival can displace an earlier one: a key keeps
    /// its first <see cref="MaxPendingGloasCandidatesPerKey"/> candidates, and a full pool refuses new ones.
    /// A candidate is kept until the end of the slot after its own, so a flood holds space only while it lasts.
    /// A flood can still deny parking at no cost to its sender, so a sampled column missing when its block arrives can be recovered only by a DataColumnSidecarsByRoot request.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="sidecar"/> names no beacon block root.</exception>
    public bool AddPendingGloas(DataColumnSidecarGloas sidecar, ulong currentSlot)
    {
        (Hash256 BlockRoot, ulong Column) key = (BlockRootOf(sidecar), sidecar.Index);

        lock (_pendingLock)
        {
            PruneStalePending(currentSlot);
            if (IsStale(sidecar, currentSlot) || _pendingCount >= _maxPendingGloas)
            {
                return false;
            }

            if (!_pendingByKey.TryGetValue(key, out List<DataColumnSidecarGloas>? candidates))
            {
                candidates = [];
                _pendingByKey[key] = candidates;
            }
            else if (candidates.Count >= MaxPendingGloasCandidatesPerKey)
            {
                return false;
            }

            candidates.Add(sidecar);
            _pendingCount++;
        }

        if (Volatile.Read(ref _watchCount) == 0)
        {
            return true;
        }

        // After the pending lock is released: watches are guarded by the served lock, which registration takes before the pending one.
        Action? wake;
        lock (_servedLock)
        {
            wake = TakeWakeOnArrival(key.BlockRoot, key.Column);
        }

        wake?.Invoke();
        return true;
    }

    /// <summary>A snapshot of the parked, unverified Gloas candidates for a block root and column, in arrival order.</summary>
    /// <remarks>The caller must verify each against the block and match its <c>slot</c> to the block's before use.</remarks>
    public DataColumnSidecarGloas[] GetPendingGloas(Hash256 blockRoot, ulong column)
    {
        lock (_pendingLock)
        {
            return _pendingByKey.TryGetValue((blockRoot, column), out List<DataColumnSidecarGloas>? candidates) ? [.. candidates] : [];
        }
    }

    /// <summary>Drops a parked candidate that failed verification against its block; a no-op if it is no longer parked.</summary>
    public void DiscardPendingGloas(DataColumnSidecarGloas sidecar)
    {
        if (sidecar.BeaconBlockRoot is not { } blockRoot) return;

        lock (_pendingLock)
        {
            if (!_pendingByKey.TryGetValue((blockRoot, sidecar.Index), out List<DataColumnSidecarGloas>? candidates)) return;

            for (int i = 0; i < candidates.Count; i++)
            {
                if (ReferenceEquals(candidates[i], sidecar))
                {
                    candidates.RemoveAt(i);
                    _pendingCount--;
                    if (candidates.Count == 0)
                    {
                        _pendingByKey.Remove((blockRoot, sidecar.Index));
                    }

                    return;
                }
            }
        }
    }

    internal int PendingGloasCount
    {
        get
        {
            lock (_pendingLock)
            {
                return _pendingCount;
            }
        }
    }

    private void PruneStalePending(ulong currentSlot)
    {
        if (currentSlot <= _pendingPrunedAtSlot)
        {
            return;
        }

        _pendingPrunedAtSlot = currentSlot;
        List<(Hash256 BlockRoot, ulong Column)>? emptied = null;
        foreach (KeyValuePair<(Hash256 BlockRoot, ulong Column), List<DataColumnSidecarGloas>> entry in _pendingByKey)
        {
            for (int i = entry.Value.Count - 1; i >= 0; i--)
            {
                if (IsStale(entry.Value[i], currentSlot))
                {
                    entry.Value.RemoveAt(i);
                    _pendingCount--;
                }
            }

            if (entry.Value.Count == 0)
            {
                (emptied ??= []).Add(entry.Key);
            }
        }

        if (emptied is not null)
        {
            foreach ((Hash256 BlockRoot, ulong Column) key in emptied)
            {
                _pendingByKey.Remove(key);
            }
        }
    }

    // A candidate outlives its own slot by one, so a block that arrives late in the next slot still finds it.
    private static bool IsStale(DataColumnSidecarGloas sidecar, ulong currentSlot) => sidecar.Slot < currentSlot && currentSlot - sidecar.Slot > 1;

    // A sidecar older than the first one given does not make the slots between them complete.
    private void MarkGiven(ulong slot) => _firstGivenSlot ??= slot;

    private static Hash256 BlockRootOf(DataColumnSidecarGloas sidecar) =>
        sidecar.BeaconBlockRoot ?? throw new ArgumentException("A Gloas data column sidecar must name its beacon block root", nameof(sidecar));

    /// <summary>
    /// Sidecars by (block root, column): at most <paramref name="capacity"/> retained lowest slot first, oldest first within a
    /// slot, plus the <paramref name="capacity"/> most recently given; not thread-safe.
    /// </summary>
    /// <remarks>
    /// The retained set is what <see cref="IncompleteBelow"/> describes. The recent set holds a sidecar the retained set
    /// refuses, so a verified range-synced column below every held slot is still found when its block is imported.
    /// </remarks>
    private sealed class SlotOrderedSidecars<TSidecar>(int capacity) where TSidecar : class
    {
        private readonly int _capacity = capacity >= 1 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "The pool must hold at least one sidecar");
        private readonly Dictionary<(Hash256 BlockRoot, ulong Column), (TSidecar Sidecar, ulong Slot, LinkedListNode<(Hash256 BlockRoot, ulong Column)> Node)> _entries = [];
        private readonly SortedDictionary<ulong, LinkedList<(Hash256 BlockRoot, ulong Column)>> _keysBySlot = [];
        private readonly LruCache<(Hash256 BlockRoot, ulong Column), TSidecar> _recent = new(capacity, "recent data column sidecars");

        /// <summary>One past the highest slot that lost or was refused a retained sidecar; every slot from here up is retained completely.</summary>
        public ulong IncompleteBelow { get; private set; }
        public int SlotCount => _keysBySlot.Count;

        public void Set((Hash256 BlockRoot, ulong Column) key, ulong slot, TSidecar sidecar)
        {
            _recent.Set(key, sidecar);
            if (_entries.TryGetValue(key, out (TSidecar Sidecar, ulong Slot, LinkedListNode<(Hash256 BlockRoot, ulong Column)> Node) held))
            {
                if (held.Slot == slot)
                {
                    _entries[key] = (sidecar, slot, held.Node);
                    return;
                }

                Remove(key, held.Slot);
            }

            if (_entries.Count >= _capacity && !TryEvictAtOrBelow(slot))
            {
                MarkIncomplete(slot);
                return;
            }

            if (!_keysBySlot.TryGetValue(slot, out LinkedList<(Hash256 BlockRoot, ulong Column)>? keys))
            {
                keys = [];
                _keysBySlot[slot] = keys;
            }

            _entries[key] = (sidecar, slot, keys.AddLast(key));
        }

        public bool TryGet((Hash256 BlockRoot, ulong Column) key, [NotNullWhen(true)] out TSidecar? sidecar)
        {
            if (_entries.TryGetValue(key, out (TSidecar Sidecar, ulong Slot, LinkedListNode<(Hash256 BlockRoot, ulong Column)> Node) held))
            {
                sidecar = held.Sidecar;
                return true;
            }

            return _recent.TryGet(key, out sidecar!) && sidecar is not null;
        }

        // A slot below the lowest held one would itself be the next eviction, so it is refused instead of displacing a higher slot.
        private bool TryEvictAtOrBelow(ulong slot)
        {
            foreach (KeyValuePair<ulong, LinkedList<(Hash256 BlockRoot, ulong Column)>> lowest in _keysBySlot)
            {
                if (slot < lowest.Key) return false;

                Remove(lowest.Value.First!.Value, lowest.Key);
                return true;
            }

            return false;
        }

        private void Remove((Hash256 BlockRoot, ulong Column) key, ulong slot)
        {
            LinkedList<(Hash256 BlockRoot, ulong Column)> keys = _keysBySlot[slot];
            keys.Remove(_entries[key].Node);
            _entries.Remove(key);
            if (keys.Count == 0)
            {
                _keysBySlot.Remove(slot);
            }

            MarkIncomplete(slot);
        }

        private void MarkIncomplete(ulong slot) => IncompleteBelow = Math.Max(IncompleteBelow, slot == ulong.MaxValue ? slot : slot + 1);
    }
}
