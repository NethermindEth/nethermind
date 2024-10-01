// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Tracing;

namespace Nethermind.State
{
    using Core.Cpu;
    /// <summary>
    /// Manages persistent storage allowing for snapshotting and restoring
    /// Persists data to ITrieStore
    /// </summary>
    internal sealed class PersistentStorageProvider : PartialStorageProviderBase
    {
        private readonly IStateOwner _owner;
        private readonly ILogManager? _logManager;

        /// <summary>
        /// EIP-1283
        /// </summary>
        private readonly Dictionary<StorageCell, byte[]> _originalValues = new();

        private readonly HashSet<StorageCell> _committedThisRound = new();
        private readonly Dictionary<AddressAsKey, SelfDestructDictionary<byte[]>> _blockCache = new(4_096);
        private readonly ConcurrentDictionary<StorageCell, byte[]>? _preBlockCache;
        private readonly Func<StorageCell, byte[]> _loadFromTree;

        /// <summary>
        /// Manages persistent storage allowing for snapshotting and restoring
        /// Persists data to ITrieStore
        /// </summary>
        public PersistentStorageProvider(IStateOwner owner,
            ILogManager logManager,
            ConcurrentDictionary<StorageCell, byte[]>? preBlockCache,
            bool populatePreBlockCache) : base(logManager)
        {
            _owner = owner;
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _preBlockCache = preBlockCache;
            _populatePreBlockCache = populatePreBlockCache;
            _loadFromTree = LoadFromTreeStorage;
        }

        private readonly bool _populatePreBlockCache;

        /// <summary>
        /// Reset the storage state
        /// </summary>
        public override void Reset(bool resizeCollections = true)
        {
            base.Reset();
            _blockCache.Clear();
            _originalValues.Clear();
            _committedThisRound.Clear();
        }

        /// <summary>
        /// Get the current value at the specified location
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <returns>Value at location</returns>
        protected override ReadOnlySpan<byte> GetCurrentValue(in StorageCell storageCell) =>
            TryGetCachedValue(storageCell, out byte[]? bytes) ? bytes! : LoadFromTree(storageCell);

        /// <summary>
        /// Return the original persistent storage value from the storage cell
        /// </summary>
        /// <param name="storageCell"></param>
        /// <returns></returns>
        public byte[] GetOriginal(in StorageCell storageCell)
        {
            if (!_originalValues.TryGetValue(storageCell, out var value))
            {
                throw new InvalidOperationException("Get original should only be called after get within the same caching round");
            }

            if (_transactionChangesSnapshots.TryPeek(out int snapshot))
            {
                if (_intraBlockCache.TryGetValue(storageCell, out StackList<int> stack))
                {
                    if (stack.TryGetSearchedItem(snapshot, out int lastChangeIndexBeforeOriginalSnapshot))
                    {
                        return _changes[lastChangeIndexBeforeOriginalSnapshot].Value;
                    }
                }
            }

            return value;
        }

        protected override void OnCellUpdatePushed(in StorageCell cell) => _owner.State.StorageMightBeSet(cell);

        private HashSet<AddressAsKey>? _tempToUpdateRoots;

        /// <summary>
        /// Called by Commit
        /// Used for persistent storage specific logic
        /// </summary>
        /// <param name="tracer">Storage tracer</param>
        protected override void CommitCore(IStorageTracer tracer)
        {
            if (_logger.IsTrace) _logger.Trace("Committing storage changes");

            int currentPosition = _changes.Count - 1;
            if (currentPosition < 0)
            {
                return;
            }
            if (_changes[currentPosition].IsNull)
            {
                throw new InvalidOperationException($"Change at current position {currentPosition} was null when committing {nameof(PartialStorageProviderBase)}");
            }


            IState state = _owner.State;

            bool isTracing = tracer.IsTracingStorage;
            Dictionary<StorageCell, ChangeTrace>? trace = null;
            if (isTracing)
            {
                trace = new Dictionary<StorageCell, ChangeTrace>();
            }

            for (int i = 0; i <= currentPosition; i++)
            {
                Change change = _changes[currentPosition - i];
                if (!isTracing && change!.ChangeType == ChangeType.JustCache)
                {
                    continue;
                }

                if (_committedThisRound.Contains(change!.StorageCell))
                {
                    if (isTracing && change.ChangeType == ChangeType.JustCache)
                    {
                        trace![change.StorageCell] = new ChangeTrace(change.Value, trace[change.StorageCell].After);
                    }

                    continue;
                }

                if (isTracing && change.ChangeType == ChangeType.JustCache)
                {
                    tracer!.ReportStorageRead(change.StorageCell);
                }

                _committedThisRound.Add(change.StorageCell);

                if (change.ChangeType == ChangeType.Destroy)
                {
                    continue;
                }

                int forAssertion = _intraBlockCache[change.StorageCell].Pop();
                if (forAssertion != currentPosition - i)
                {
                    throw new InvalidOperationException($"Expected checked value {forAssertion} to be equal to {currentPosition} - {i}");
                }

                switch (change.ChangeType)
                {
                    case ChangeType.Destroy:
                        break;
                    case ChangeType.JustCache:
                        break;
                    case ChangeType.Update:
                        if (_logger.IsTrace)
                        {
                            _logger.Trace($"  Update {change.StorageCell.Address}_{change.StorageCell.Index} V = {change.Value.ToHexString(true)}");
                        }

                        SaveToTree(state, change);

                        if (isTracing)
                        {
                            trace![change.StorageCell] = new ChangeTrace(change.Value);
                        }

                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            base.CommitCore(tracer);
            _originalValues.Clear();
            _committedThisRound.Clear();

            if (isTracing)
            {
                ReportChanges(tracer!, trace!);
            }
        }

        private void SaveToTree(IState state, Change change)
        {
            if (_originalValues.TryGetValue(change.StorageCell, out byte[] initialValue) &&
                initialValue.AsSpan().SequenceEqual(change.Value))
            {
                // no need to update the tree if the value is the same
                return;
            }

            state.SetStorage(change.StorageCell, change.Value);
            Db.Metrics.StorageTreeWrites++;

            ref SelfDestructDictionary<byte[]>? dict = ref CollectionsMarshal.GetValueRefOrAddDefault(_blockCache, change.StorageCell.Address, out bool exists);
            if (!exists)
            {
                dict = new SelfDestructDictionary<byte[]>(StorageTree.EmptyBytes);
            }

            dict[change.StorageCell.Index] = change.Value;
        }

        public void WarmUp(in StorageCell storageCell, bool isEmpty)
        {
            if (isEmpty)
            {
                if (_preBlockCache is not null)
                {
                    _preBlockCache[storageCell] = [];
                }
            }
            else
            {
                LoadFromTree(in storageCell);
            }
        }

        private ReadOnlySpan<byte> LoadFromTree(in StorageCell storageCell)
        {
            ref SelfDestructDictionary<byte[]>? dict = ref CollectionsMarshal.GetValueRefOrAddDefault(_blockCache, storageCell.Address, out bool exists);
            if (!exists)
            {
                dict = new SelfDestructDictionary<byte[]>(StorageTree.EmptyBytes);
            }

            ref byte[]? value = ref dict.GetValueRefOrAddDefault(storageCell.Index, out exists);
            if (!exists)
            {
                value = !_populatePreBlockCache ?
                    LoadFromTreeReadPreWarmCache(in storageCell) :
                    LoadFromTreePopulatePrewarmCache(in storageCell);
            }
            else
            {
                Db.Metrics.IncrementStorageTreeCache();
            }

            if (!storageCell.IsHash) PushToRegistryOnly(storageCell, value);
            return value;
        }

        private byte[] LoadFromTreePopulatePrewarmCache(in StorageCell storageCell)
        {
            long priorReads = Db.Metrics.ThreadLocalStorageTreeReads;

            byte[] value = _preBlockCache is not null
                ? _preBlockCache.GetOrAdd(storageCell, _loadFromTree)
                : _loadFromTree(storageCell);

            if (Db.Metrics.ThreadLocalStorageTreeReads == priorReads)
            {
                // Read from Concurrent Cache
                Db.Metrics.IncrementStorageTreeCache();
            }
            return value;
        }

        private byte[] LoadFromTreeReadPreWarmCache(in StorageCell storageCell)
        {
            if (_preBlockCache?.TryGetValue(storageCell, out byte[] value) ?? false)
            {
                Db.Metrics.IncrementStorageTreeCache();
            }
            else
            {
                value = _loadFromTree(storageCell);
            }
            return value;
        }

        private byte[] LoadFromTreeStorage(StorageCell storageCell)
        {
            Db.Metrics.IncrementStorageTreeReads();

            // TODO: remove ToArray materialization
            return _owner.State.GetStorageAt(storageCell).ToArray();
        }

        private void PushToRegistryOnly(in StorageCell cell, byte[] value)
        {
            StackList<int> stack = SetupRegistry(cell);
            _originalValues[cell] = value;
            stack.Push(_changes.Count);
            _changes.Add(new Change(ChangeType.JustCache, cell, value));
        }

        private static void ReportChanges(IStorageTracer tracer, Dictionary<StorageCell, ChangeTrace> trace)
        {
            foreach ((StorageCell address, ChangeTrace change) in trace)
            {
                byte[] before = change.Before;
                byte[] after = change.After;

                if (!Bytes.AreEqual(before, after))
                {
                    tracer.ReportStorageChange(address, before, after);
                }
            }
        }

        /// <summary>
        /// Clear all storage at specified address
        /// </summary>
        /// <param name="address">Contract address</param>
        public override void ClearStorage(Address address)
        {
            base.ClearStorage(address);

            ref SelfDestructDictionary<byte[]>? dict = ref CollectionsMarshal.GetValueRefOrAddDefault(_blockCache, address, out bool exists);
            if (!exists)
            {
                dict = new SelfDestructDictionary<byte[]>(StorageTree.EmptyBytes);
            }

            dict.SelfDestruct();
        }

        private sealed class SelfDestructDictionary<TValue>(TValue destructedValue)
        {
            private bool _selfDestruct;
            private readonly Dictionary<UInt256, TValue> _dictionary = new(Comparer.Instance);

            public void SelfDestruct()
            {
                _selfDestruct = true;
                _dictionary.Clear();
            }

            public ref TValue? GetValueRefOrAddDefault(UInt256 storageCellIndex, out bool exists)
            {
                ref TValue value = ref CollectionsMarshal.GetValueRefOrAddDefault(_dictionary, storageCellIndex, out exists);
                if (!exists && _selfDestruct)
                {
                    value = destructedValue;
                    exists = true;
                }
                return ref value;
            }

            public TValue? this[UInt256 key]
            {
                set => _dictionary[key] = value;
            }

            private sealed class Comparer : IEqualityComparer<UInt256>
            {
                public static Comparer Instance { get; } = new();

                private Comparer() { }

                public bool Equals(UInt256 x, UInt256 y)
                    => Unsafe.As<UInt256, Vector256<byte>>(ref x) == Unsafe.As<UInt256, Vector256<byte>>(ref y);

                public int GetHashCode([DisallowNull] UInt256 obj)
                    => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in obj, 1)).FastHash();
            }
        }
    }
}
