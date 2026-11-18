// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Stats.Model;

[assembly: InternalsVisibleTo("Nethermind.Synchronization.Test")]

namespace Nethermind.Synchronization.Peers
{
    /// <summary>Tracks a peer's advertised head, allocations and evidence from synchronization responses.</summary>
    /// <remarks>
    /// No-progress and timeout evidence is scoped to allocation contexts and expires after five minutes.
    /// Verified useful work clears only its matching contexts. Refresh and allocated timeouts have separate origins;
    /// already outstanding allocated requests share a completion fence. The first two timeouts are transient weakness,
    /// and the third is no-progress evidence. Only Headers, Bodies, Receipts and ForwardHeader faults influence
    /// expected-data eviction; State, Snap and BlockAccessLists faults do not. Trusted refresh responses clear only
    /// refresh timeouts; advertised heads do not establish served height.
    /// </remarks>
    public class PeerInfo(ISyncPeer syncPeer, AllocationAllowances? allocationAllowances = null)
    {
        public const int SleepThreshold = 2;

        private readonly TimeProvider _timeProvider = TimeProvider.System;
        private readonly long _createdTimestamp = TimeProvider.System.GetTimestamp();
        // Match the existing ten-second consensus refresh cadence without crediting an announcement as delivery.
        private static readonly TimeSpan InitialRefreshInterval = TimeSpan.FromSeconds(10);

        private readonly IBlockTree? _blockTree;
        private readonly ulong _configuredPivotNumber;

        internal PeerInfo(ISyncPeer syncPeer, AllocationAllowances? allocationAllowances, TimeProvider timeProvider,
            IBlockTree? blockTree = null, ulong configuredPivotNumber = 0)
            : this(syncPeer, allocationAllowances)
        {
            _timeProvider = timeProvider;
            _createdTimestamp = timeProvider.GetTimestamp();
            _blockTree = blockTree;
            _configuredPivotNumber = configuredPivotNumber;
        }

        private readonly AllocationAllowances _allocationAllowances = allocationAllowances ?? AllocationAllowances.Default;
        private readonly ulong _maxPacked = (allocationAllowances ?? AllocationAllowances.Default).Pack();
        private ulong _availableSlots = (allocationAllowances ?? AllocationAllowances.Default).Pack();
        private uint _weaknesses;
        private uint _qualityContexts;
        private ProgressTimestamps _qualitySince;
        private uint _sleepingContexts;
        private readonly Lock _sleepLock = new();
        private uint _noProgressContexts;
        private ProgressTimestamps _noProgressSince;
        private const AllocationContexts TimeoutContexts = AllocationContexts.Headers | AllocationContexts.Bodies
            | AllocationContexts.Receipts | AllocationContexts.ForwardHeader;
        private static readonly TimeSpan EvidenceLifetime = TimeSpan.FromMinutes(5);
        private byte _refreshTimeoutCount;
        private long _refreshTimeoutTimestamp;
        private ulong _issuedRequestOrdinal;
        private AllocatedTimeouts _allocatedTimeouts;
        private HistoryEvidence _bodiesHistory;
        private HistoryEvidence _receiptsHistory;

        private struct HistoryEvidence
        {
            public byte EmptyCount;
            public long EmptyTimestamp;
            public ulong ServedNumber;
            public ulong Floor;
            public long FloorTimestamp;
        }

        [InlineArray(4)]
        private struct AllocatedTimeouts
        {
            private AllocatedTimeout _timeout;
        }

        private struct AllocatedTimeout
        {
            public byte Count;
            public long Timestamp;
            public ulong CompletionFence;
        }

        [InlineArray(AllocationAllowances.SingleContextCount)]
        private struct ProgressTimestamps
        {
            private long _timestamp;
        }
        private ulong _servedHeadNumber;

        private ulong _lastNotifiedEarliestNumber;
        private ulong _lastNotifiedLatestNumber;

        public NodeClientType PeerClientType => SyncPeer?.ClientType ?? NodeClientType.Unknown;

        public ISyncPeer SyncPeer { get; } = syncPeer;

        public bool IsInitialized => SyncPeer.IsInitialized;

        /// <summary>See <see cref="ISyncPeer.TotalDifficulty"/>.</summary>
        public UInt256? TotalDifficulty => SyncPeer.TotalDifficulty;

        public ulong HeadNumber
        {
            get
            {
                if (_blockTree is null) return SyncPeer.HeadNumber;
                ulong reference = Math.Max(_blockTree.BestKnownNumber, Math.Max(_blockTree.SyncPivot.BlockNumber, _configuredPivotNumber));
                ulong horizon = reference > ulong.MaxValue - 1024 ? ulong.MaxValue : reference + 1024;
                return Math.Min(SyncPeer.HeadNumber, Math.Max(ServedHeadNumber, horizon));
            }
        }

        internal ulong? KnownHeadNumber => _blockTree?.BestKnownNumber;
        internal ulong EvictionHeadNumber => Math.Min(SyncPeer.HeadNumber, Math.Max(ServedHeadNumber, _blockTree?.BestKnownNumber ?? 0));
        public Hash256 HeadHash => SyncPeer.HeadHash;

        internal ulong ServedHeadNumber => Volatile.Read(ref _servedHeadNumber);
        internal bool IsWithinInitialRefreshInterval =>
            _timeProvider.GetElapsedTime(_createdTimestamp, _timeProvider.GetTimestamp()) < InitialRefreshInterval;

        internal bool HasNoProgress(AllocationContexts contexts)
        {
            lock (_sleepLock)
            {
                long now = _timeProvider.GetTimestamp();
                for (uint bits = _noProgressContexts; bits != 0; bits &= bits - 1)
                {
                    int index = BitOperations.TrailingZeroCount(bits);
                    if (_timeProvider.GetElapsedTime(_noProgressSince[index], now) >= EvidenceLifetime)
                        _noProgressContexts &= ~(1u << index);
                }
                return (_noProgressContexts & (uint)contexts) != 0 || HasTimeout(contexts, 3);
            }
        }

        internal void RecordNoProgress(AllocationContexts contexts)
        {
            lock (_sleepLock)
            {
                long now = _timeProvider.GetTimestamp();
                for (uint bits = (uint)contexts; bits != 0; bits &= bits - 1)
                    _noProgressSince[BitOperations.TrailingZeroCount(bits)] = now;
                Volatile.Write(ref _noProgressContexts, _noProgressContexts | (uint)contexts);
            }
        }

        /// <summary>Clears ordinary failure evidence and allocated timeouts only for verified useful contexts.</summary>
        /// <remarks>Refresh timeouts have a separate origin and are cleared only by a trusted refresh response.</remarks>
        internal void RecordProgress(AllocationContexts contexts)
        {
            lock (_sleepLock)
            {
                _qualityContexts &= ~(uint)contexts;
                Volatile.Write(ref _noProgressContexts, _noProgressContexts & ~(uint)contexts);
                for (uint bits = (uint)(contexts & TimeoutContexts); bits != 0; bits &= bits - 1)
                {
                    ref AllocatedTimeout timeout = ref _allocatedTimeouts[TimeoutIndex((AllocationContexts)(1u << BitOperations.TrailingZeroCount(bits)))];
                    timeout.Count = 0;
                    timeout.CompletionFence = _issuedRequestOrdinal;
                }
            }
            Interlocked.And(ref _weaknesses, ~WeaknessTracking.ClearMaskFor(contexts));
        }

        internal void DemoteHeadClaim(Hash256 requestedHash, ulong claimedNumber) =>
            SyncPeer.TrySetHead(requestedHash, claimedNumber, requestedHash, ServedHeadNumber);

        internal bool HasWeakness(AllocationContexts contexts)
        {
            if (HasNoProgress(contexts)) return true;
            lock (_sleepLock)
            {
                long now = _timeProvider.GetTimestamp();
                for (uint bits = _qualityContexts & (uint)contexts; bits != 0; bits &= bits - 1)
                {
                    int index = BitOperations.TrailingZeroCount(bits);
                    if (_timeProvider.GetElapsedTime(_qualitySince[index], now) < EvidenceLifetime) return true;
                    _qualityContexts &= ~(1u << index);
                }
                return HasTimeout(contexts, 1);
            }
        }

        internal bool HasTimeoutEvidence
        {
            get { lock (_sleepLock) return HasTimeout(TimeoutContexts, 1); }
        }

        internal ulong CaptureRequestOrdinal()
        {
            lock (_sleepLock)
            {
                if (_issuedRequestOrdinal != ulong.MaxValue) _issuedRequestOrdinal++;
                return _issuedRequestOrdinal;
            }
        }

        internal void RecordAllocatedTimeout(AllocationContexts contexts, ulong requestOrdinal)
        {
            lock (_sleepLock)
            {
                long now = _timeProvider.GetTimestamp();
                for (uint bits = (uint)(contexts & TimeoutContexts); bits != 0; bits &= bits - 1)
                {
                    ref AllocatedTimeout timeout = ref _allocatedTimeouts[TimeoutIndex((AllocationContexts)(1u << BitOperations.TrailingZeroCount(bits)))];
                    if (_timeProvider.GetElapsedTime(timeout.Timestamp, now) >= EvidenceLifetime) timeout.Count = 0;
                    if (requestOrdinal > timeout.CompletionFence)
                    {
                        timeout.Count = (byte)Math.Min(3, timeout.Count + 1);
                        timeout.CompletionFence = _issuedRequestOrdinal;
                    }
                    timeout.Timestamp = now;
                }
            }
        }

        internal int RecordRefreshTimeout()
        {
            lock (_sleepLock)
            {
                long now = _timeProvider.GetTimestamp();
                if (_timeProvider.GetElapsedTime(_refreshTimeoutTimestamp, now) >= EvidenceLifetime) _refreshTimeoutCount = 0;
                _refreshTimeoutCount = (byte)Math.Min(3, _refreshTimeoutCount + 1);
                _refreshTimeoutTimestamp = now;
                return _refreshTimeoutCount;
            }
        }

        internal void RecordRefreshResponse()
        {
            lock (_sleepLock) _refreshTimeoutCount = 0;
        }

        private bool HasTimeout(AllocationContexts contexts, int threshold)
        {
            long now = _timeProvider.GetTimestamp();
            if (_timeProvider.GetElapsedTime(_refreshTimeoutTimestamp, now) >= EvidenceLifetime) _refreshTimeoutCount = 0;
            bool result = (contexts & AllocationContexts.Headers) != 0 && _refreshTimeoutCount >= threshold;
            for (uint bits = (uint)(contexts & TimeoutContexts); bits != 0; bits &= bits - 1)
            {
                ref AllocatedTimeout timeout = ref _allocatedTimeouts[TimeoutIndex((AllocationContexts)(1u << BitOperations.TrailingZeroCount(bits)))];
                if (_timeProvider.GetElapsedTime(timeout.Timestamp, now) >= EvidenceLifetime) timeout.Count = 0;
                result |= timeout.Count >= threshold;
            }
            return result;
        }

        private static int TimeoutIndex(AllocationContexts context) => context switch
        {
            AllocationContexts.Headers => 0,
            AllocationContexts.Bodies => 1,
            AllocationContexts.Receipts => 2,
            AllocationContexts.ForwardHeader => 3,
            _ => ThrowInvalidTimeoutContext(context),
        };

        [DoesNotReturn, StackTraceHidden]
        private static int ThrowInvalidTimeoutContext(AllocationContexts context) =>
            throw new ArgumentOutOfRangeException(nameof(context));

        internal void RecordHistoryData(AllocationContexts context, ulong number)
        {
            lock (_sleepLock)
            {
                ref HistoryEvidence history = ref GetHistory(context);
                history.ServedNumber = Math.Max(history.ServedNumber, number);
                history.EmptyCount = 0;
                if (number < history.Floor)
                {
                    history.Floor = number;
                    history.FloorTimestamp = _timeProvider.GetTimestamp();
                }
            }
        }

        internal ulong HistoryFloor(AllocationContexts context)
        {
            lock (_sleepLock)
            {
                ref HistoryEvidence history = ref GetHistory(context);
                if (_timeProvider.GetElapsedTime(history.FloorTimestamp, _timeProvider.GetTimestamp()) >= EvidenceLifetime)
                    history.Floor = 0;
                return history.Floor;
            }
        }

        internal bool RecordHistoryEmpty(AllocationContexts context, ReadOnlySpan<BlockInfo?> infos)
        {
            ulong? highest = null;
            foreach (BlockInfo? info in infos)
                if (info is not null) highest = highest is null ? info.BlockNumber : Math.Max(highest.Value, info.BlockNumber);
            if (highest is null) return false;
            lock (_sleepLock)
            {
                ref HistoryEvidence history = ref GetHistory(context);
                long now = _timeProvider.GetTimestamp();
                if (_timeProvider.GetElapsedTime(history.FloorTimestamp, now) >= EvidenceLifetime) history.Floor = 0;
                if (highest < history.ServedNumber)
                {
                    ulong floor = highest.Value + 1;
                    if (floor > history.Floor)
                    {
                        history.Floor = floor;
                        history.FloorTimestamp = now;
                    }
                    return true;
                }
                if (_timeProvider.GetElapsedTime(history.EmptyTimestamp, now) >= EvidenceLifetime) history.EmptyCount = 0;
                history.EmptyTimestamp = now;
                history.EmptyCount = (byte)Math.Min(4, history.EmptyCount + 1);
                return history.EmptyCount <= 3;
            }
        }

        private ref HistoryEvidence GetHistory(AllocationContexts context)
        {
            if (context == AllocationContexts.Bodies) return ref _bodiesHistory;
            if (context == AllocationContexts.Receipts) return ref _receiptsHistory;
            return ref ThrowInvalidHistoryContext(context);
        }

        [DoesNotReturn, StackTraceHidden]
        private static ref HistoryEvidence ThrowInvalidHistoryContext(AllocationContexts context) =>
            throw new ArgumentOutOfRangeException(nameof(context));

        internal void RecordServedHead(ulong number)
        {
            ulong previous = ServedHeadNumber;
            while (previous < number)
            {
                ulong observed = Interlocked.CompareExchange(ref _servedHeadNumber, number, previous);
                if (observed == previous) break;
                previous = observed;
            }
        }

        public AllocationContexts SleepingContexts => (AllocationContexts)Volatile.Read(ref _sleepingContexts);

        private SleepSlots _sleepingSince;

        [InlineArray(WeaknessTracking.TrackedContextCount)]
        private struct SleepSlots
        {
            private SleepSlot? _slot;
        }

        private sealed class SleepSlot(DateTime since)
        {
            public DateTime Since { get; } = since;
        }

        public AllocationContexts AllocatedContexts =>
            AllocationAllowances.AllocatedFrom(ReadSlots(), _maxPacked);

        public AllocationAllowances AvailableAllocationSlots =>
            AllocationAllowances.Unpack(ReadSlots());

        public bool CanBeAllocated(AllocationContexts contexts) =>
            !IsAsleep(contexts) && !IsAllocationFull(contexts) && this.SupportsAllocation(contexts);

        public bool IsAsleep(AllocationContexts contexts) =>
            ((AllocationContexts)Volatile.Read(ref _sleepingContexts) & contexts) != AllocationContexts.None;

        /// <summary>True if any single-bit context in <paramref name="contexts"/> currently has at least one slot taken.</summary>
        public bool IsAllocated(AllocationContexts contexts) =>
            AllocationAllowances.AnyAllocated(ReadSlots(), _maxPacked, contexts);

        /// <summary>True if any single-bit context in <paramref name="contexts"/> has no remaining slots.</summary>
        public bool IsAllocationFull(AllocationContexts contexts) =>
            AllocationAllowances.AnyFull(ReadSlots(), contexts);

        public bool TryAllocate(AllocationContexts contexts)
        {
            if (IsAsleep(contexts) || !this.SupportsAllocation(contexts)) return false;

            // Vacuous success when the request carries no single-bit contexts (e.g. AllocationContexts.None);
            // matches the bitmask-era behaviour callers depend on.
            ulong delta = AllocationAllowances.BuildSlotDelta(contexts);
            if (delta == 0) return true;
            if (!TryDecrementSlots(delta)) return false;

            // Lock-free TOCTOU close-out: the IsAsleep check above and the CAS are not atomic, so a concurrent
            // PutToSleep can fire after we've taken the slots. Re-check and roll back so a sleeping peer never
            // holds an active allocation slot once the sleeping flag is observable.
            if (IsAsleep(contexts))
            {
                Free(contexts);
                return false;
            }
            return true;
        }

        public void Free(AllocationContexts contexts) =>
            IncrementSlotsBounded(AllocationAllowances.BuildSlotDelta(contexts));

        public void PutToSleep(AllocationContexts contexts, DateTime dateTime)
        {
            lock (_sleepLock)
            {
                Volatile.Write(ref _sleepingContexts, _sleepingContexts | (uint)contexts);
                SleepSlot slot = new(dateTime);
                for (uint bits = (uint)contexts; bits != 0; bits &= bits - 1)
                {
                    AllocationContexts ctx = (AllocationContexts)(1u << BitOperations.TrailingZeroCount(bits));
                    _sleepingSince[WeaknessTracking.IndexOf(ctx)] = slot;
                }
                if ((contexts & AllocationContexts.Blocks) == AllocationContexts.Blocks)
                {
                    _sleepingSince[WeaknessTracking.IndexOf(AllocationContexts.Blocks)] = slot;
                }
            }
        }

        public void TryToWakeUp(DateTime dateTime, TimeSpan wakeUpIfSleepsMoreThanThis) =>
            TryToWakeUp(dateTime, wakeUpIfSleepsMoreThanThis, limitBackoff: false);

        internal void TryToWakeUp(DateTime dateTime, TimeSpan wakeUpIfSleepsMoreThanThis, bool limitBackoff)
        {
            lock (_sleepLock)
            {
                uint sleepingContexts = 0;
                for (int i = 0; i < WeaknessTracking.TrackedContextCount; i++)
                {
                    SleepSlot? slot = _sleepingSince[i];
                    if (slot is null) continue;
                    int failures = (int)((Volatile.Read(ref _weaknesses) >> (i * WeaknessTracking.WeaknessBits)) & WeaknessTracking.WeaknessNibbleMask);
                    int backoff = 1 << Math.Clamp(failures - SleepThreshold, 0, limitBackoff ? 1 : 6);
                    if ((dateTime - slot.Since).Ticks / backoff >= wakeUpIfSleepsMoreThanThis.Ticks)
                        _sleepingSince[i] = null;
                    else
                        sleepingContexts |= (uint)WeaknessTracking.ContextAt(i);
                }
                // Blocks overlaps Bodies and Receipts; an expired slot cannot clear another live slot's bits.
                Volatile.Write(ref _sleepingContexts, sleepingContexts);
            }
        }

        public AllocationContexts IncreaseWeakness(AllocationContexts requested) => IncreaseWeakness(requested, false);

        internal AllocationContexts IncreaseWeakness(AllocationContexts requested, bool transient)
        {
            uint delta = WeaknessTracking.BuildDelta(requested);
            if (delta == 0) return AllocationContexts.None;
            if (!transient)
            {
                lock (_sleepLock)
                {
                    uint contexts = (uint)(requested & (AllocationContexts.All | AllocationContexts.ForwardHeader));
                    long now = _timeProvider.GetTimestamp();
                    for (uint bits = contexts; bits != 0; bits &= bits - 1)
                        _qualitySince[BitOperations.TrailingZeroCount(bits)] = now;
                    _qualityContexts |= contexts;
                }
            }

            uint updated = AddWeaknessSaturating(delta);
            return WeaknessTracking.AtOrAboveThreshold(updated, requested, SleepThreshold);
        }

        public bool HasEqualOrBetterTDOrBlock(PeerInfo? other)
        {
            if (other is null) return true;
            if (TotalDifficulty is null || other.TotalDifficulty is null) return HeadNumber >= other.HeadNumber;
            return TotalDifficulty >= other.TotalDifficulty;
        }

        public void EnsureInitialized()
        {
            if (!IsInitialized)
                throw new InvalidAsynchronousStateException($"{GetType().Name} found an uninitialized peer - {this}");
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool ShouldNotifyNewRange(ulong earliestNumber, ulong latestNumber)
        {
            // Also notify if same header as could be reorg with different hash
            if (latestNumber < _lastNotifiedLatestNumber && earliestNumber < _lastNotifiedEarliestNumber)
                return false;
            _lastNotifiedEarliestNumber = earliestNumber;
            _lastNotifiedLatestNumber = latestNumber;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ulong ReadSlots() => Volatile.Read(ref _availableSlots);

        /// <summary>
        /// Atomically subtract <paramref name="delta"/> from the packed slot word; fail if any participating slot is zero.
        /// </summary>
        private bool TryDecrementSlots(ulong delta)
        {
            ulong old = ReadSlots();
            while (true)
            {
                if (AllocationAllowances.HasZeroSlot(old, delta)) return false;
                ulong updated = old - delta;
                ulong observed = Interlocked.CompareExchange(ref _availableSlots, updated, old);
                if (observed == old) return true;
                old = observed;
            }
        }

        /// <summary>
        /// Atomically add <paramref name="delta"/> to the packed slot word, saturating each slot at its allowance ceiling.
        /// </summary>
        private void IncrementSlotsBounded(ulong delta)
        {
            if (delta == 0) return;
            ulong old = ReadSlots();
            while (true)
            {
                ulong increments = delta & ~AllocationAllowances.CarryOnSaturation(old, delta, _maxPacked);
                if (increments == 0) return;
                ulong updated = old + increments;
                ulong observed = Interlocked.CompareExchange(ref _availableSlots, updated, old);
                if (observed == old) return;
                old = observed;
            }
        }

        private uint AddWeaknessSaturating(uint delta)
        {
            uint old = Volatile.Read(ref _weaknesses);
            while (true)
            {
                uint updated = WeaknessTracking.SaturatingAdd(old, delta);
                uint observed = Interlocked.CompareExchange(ref _weaknesses, updated, old);
                if (observed == old) return updated;
                old = observed;
            }
        }

        // Per-single-bit-context glyphs in the order of AllocationAllowances.OrderedSingleContexts.
        private static ReadOnlySpan<char> ContextChars => ['H', 'B', 'R', 'N', 'S', 'F', 'A'];

        private static string BuildContextString(AllocationContexts contexts)
        {
            Span<char> buf = stackalloc char[AllocationAllowances.SingleContextCount];
            for (int i = 0; i < AllocationAllowances.SingleContextCount; i++)
            {
                AllocationContexts ctx = AllocationAllowances.OrderedSingleContexts[i];
                buf[i] = (contexts & ctx) == ctx ? ContextChars[i] : ' ';
            }
            return new string(buf);
        }

        private string BuildSlotContextString()
        {
            AllocationAllowances available = AllocationAllowances.Unpack(ReadSlots());
            Span<char> buf = stackalloc char[AllocationAllowances.SingleContextCount];
            for (int i = 0; i < AllocationAllowances.SingleContextCount; i++)
            {
                AllocationContexts ctx = AllocationAllowances.OrderedSingleContexts[i];
                int avail = available[ctx];
                int taken = _allocationAllowances[ctx] - avail;
                // Letter marks "in flight" — single take or saturated; digit shows multi-take partial fill.
                buf[i] = (taken, avail) switch
                {
                    ( <= 0, _) => ' ',
                    (1, _) or (_, 0) => ContextChars[i],
                    _ => (char)('0' + Math.Min(taken, 9)),
                };
            }
            return new string(buf);
        }

        public override string ToString() => $"[{BuildSlotContextString()} ][{BuildContextString(SleepingContexts)} ]{SyncPeer}";
    }
}
