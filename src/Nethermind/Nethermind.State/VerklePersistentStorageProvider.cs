// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Resettables;
using Nethermind.Core.Verkle;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Tracing;

namespace Nethermind.State;

internal class VerklePersistentStorageProvider : PartialStorageProviderBase
{
    protected readonly HashSet<Address> _selfDestructAddress = new HashSet<Address>();
    protected readonly VerkleStateTree _verkleTree;
    private readonly ILogManager? _logManager;
    /// <summary>
    /// EIP-1283
    /// </summary>
    private readonly ResettableDictionary<StorageCell, byte[]> _originalValues = new ResettableDictionary<StorageCell, byte[]>();
    private readonly HashSet<StorageCell> _committedThisRound = new HashSet<StorageCell>();

    private readonly Dictionary<AddressAsKey, DefaultableDictionary> _blockChanges = new(4_096);
    private readonly ConcurrentDictionary<StorageCell, byte[]>? _preBlockCache;
    private readonly bool _populatePreBlockCache;

    public VerklePersistentStorageProvider(VerkleStateTree tree, ConcurrentDictionary<StorageCell, byte[]>? preBlockCache, bool populatePreBlockCache, ILogManager? logManager)
        : base(logManager)
    {
        _preBlockCache = preBlockCache;
        _verkleTree = tree;
        _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        _populatePreBlockCache = populatePreBlockCache;
    }

    /// <summary>
    /// Reset the storage state
    /// </summary>
    public override void Reset(bool resetBlockChanges = true)
    {
        base.Reset(resetBlockChanges);
        _originalValues.Clear();
        _committedThisRound.Clear();
        _selfDestructAddress.Clear();
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
    public byte[] GetOriginal(StorageCell storageCell)
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
                    return _changes[lastChangeIndexBeforeOriginalSnapshot]!.Value;
                }
            }
        }

        return value;
    }


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

        bool isTracing = tracer.IsTracingStorage;
        Dictionary<StorageCell, ChangeTrace>? trace = null;
        if (isTracing)
        {
            trace = new Dictionary<StorageCell, ChangeTrace>();
        }

        var toSet = new Dictionary<StorageCell, byte[]>();

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

                    if (!_selfDestructAddress.Contains(change.StorageCell.Address))
                    {
                        if (change.Value.IsZero() && _originalValues[change.StorageCell].IsZero())
                        {
                            return;
                        }
                        toSet[change.StorageCell] = change.Value;
                    }

                    if (isTracing)
                    {
                        trace![change.StorageCell] = new ChangeTrace(change.Value);
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        _verkleTree.BulkSet(toSet);

        _verkleTree.Commit();
        base.CommitCore(tracer);
        _originalValues.Reset();
        _committedThisRound.Clear();
        _selfDestructAddress.Clear();

        if (isTracing)
        {
            ReportChanges(tracer!, trace!);
        }
    }

    /// <summary>
    /// Commit persistent storage trees
    /// </summary>
    /// <param name="blockNumber">Current block number</param>
    public void CommitTrees(long blockNumber) { }

    protected virtual ReadOnlySpan<byte> LoadFromTree(in StorageCell storageCell)
    {
        Hash256? key = AccountHeader.GetTreeKeyForStorageSlot(storageCell.Address.Bytes, storageCell.Index);
        var value = (_verkleTree.Get(key) ?? []).ToArray();
        PushToRegistryOnly(storageCell, value);
        return value;
    }

    protected void PushToRegistryOnly(StorageCell cell, byte[] value)
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

    public override void ClearStorage(Address address)
    {
        _selfDestructAddress.Add(address);
    }

    private sealed class DefaultableDictionary()
    {
        private bool _missingAreDefault;
        private readonly Dictionary<UInt256, ChangeTrace> _dictionary = new(Comparer.Instance);

        public void ClearAndSetMissingAsDefault()
        {
            _missingAreDefault = true;
            _dictionary.Clear();
        }

        public ref ChangeTrace GetValueRefOrAddDefault(UInt256 storageCellIndex, out bool exists)
        {
            ref ChangeTrace value = ref CollectionsMarshal.GetValueRefOrAddDefault(_dictionary, storageCellIndex, out exists);
            if (!exists && _missingAreDefault)
            {
                // Where we know the rest of the tree is empty
                // we can say the value was found but is default
                // rather than having to check the database
                value = ChangeTrace.ZeroBytes;
                exists = true;
            }
            return ref value;
        }

        public ref ChangeTrace GetValueRefOrNullRef(UInt256 storageCellIndex)
            => ref CollectionsMarshal.GetValueRefOrNullRef(_dictionary, storageCellIndex);

        public ChangeTrace this[UInt256 key]
        {
            set => _dictionary[key] = value;
        }

        public Dictionary<UInt256, ChangeTrace>.Enumerator GetEnumerator() => _dictionary.GetEnumerator();

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
