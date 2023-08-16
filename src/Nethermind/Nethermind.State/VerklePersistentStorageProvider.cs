// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Resettables;
using Nethermind.Core.Verkle;
using Nethermind.Logging;
using Nethermind.State.Tracing;
using Nethermind.Verkle.Tree;
using Nethermind.Verkle.Tree.Utils;

namespace Nethermind.State;

internal class VerklePersistentStorageProvider : PartialStorageProviderBase
{
    private readonly VerkleStateTree _verkleTree;
    private readonly ILogManager? _logManager;
    /// <summary>
    /// EIP-1283
    /// </summary>
    private readonly ResettableDictionary<StorageCell, byte[]> _originalValues = new ResettableDictionary<StorageCell, byte[]>();
    private readonly ResettableHashSet<StorageCell> _committedThisRound = new ResettableHashSet<StorageCell>();

    public VerklePersistentStorageProvider(VerkleStateTree tree, ILogManager? logManager)
        : base(logManager)
    {
        _verkleTree = tree;
        _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
    }

    /// <summary>
    /// Reset the storage state
    /// </summary>
    public override void Reset()
    {
        base.Reset();
        _originalValues.Clear();
        _committedThisRound.Clear();
    }

    /// <summary>
    /// Get the current value at the specified location
    /// </summary>
    /// <param name="storageCell">Storage location</param>
    /// <returns>Value at location</returns>
    protected override byte[] GetCurrentValue(in StorageCell storageCell) =>
        TryGetCachedValue(storageCell, out byte[]? bytes) ? bytes! : LoadFromTree(storageCell);

    /// <summary>
    /// Return the original persistent storage value from the storage cell
    /// </summary>
    /// <param name="storageCell"></param>
    /// <returns></returns>
    public byte[] GetOriginal(StorageCell storageCell)
    {
        if (!_originalValues.ContainsKey(storageCell))
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

        return _originalValues[storageCell];
    }


    /// <summary>
    /// Called by Commit
    /// Used for persistent storage specific logic
    /// </summary>
    /// <param name="tracer">Storage tracer</param>
    protected override void CommitCore(IStorageTracer tracer)
    {
        if (_logger.IsTrace) _logger.Trace("Committing storage changes");

        if (_changes[_currentPosition] is null)
        {
            throw new InvalidOperationException($"Change at current position {_currentPosition} was null when commiting {nameof(PartialStorageProviderBase)}");
        }

        if (_changes[_currentPosition + 1] is not null)
        {
            throw new InvalidOperationException($"Change after current position ({_currentPosition} + 1) was not null when commiting {nameof(PartialStorageProviderBase)}");
        }

        bool isTracing = tracer.IsTracingStorage;
        Dictionary<StorageCell, ChangeTrace>? trace = null;
        if (isTracing)
        {
            trace = new Dictionary<StorageCell, ChangeTrace>();
        }

        for (int i = 0; i <= _currentPosition; i++)
        {
            Change change = _changes[_currentPosition - i];
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
            if (forAssertion != _currentPosition - i)
            {
                throw new InvalidOperationException($"Expected checked value {forAssertion} to be equal to {_currentPosition} - {i}");
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

                    Db.Metrics.StorageTreeWrites++;
                    _verkleTree.SetStorage(change.StorageCell, change.Value);
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
        _originalValues.Reset();
        _committedThisRound.Reset();

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

    private byte[] LoadFromTree(StorageCell storageCell)
    {
        Db.Metrics.StorageTreeReads++;
        Pedersen key = AccountHeader.GetTreeKeyForStorageSlot(storageCell.Address.Bytes, storageCell.Index);
        byte[] value = (_verkleTree.Get(key) ?? Array.Empty<byte>()).ToArray();
        PushToRegistryOnly(storageCell, value);
        return value;
    }

    private void PushToRegistryOnly(StorageCell cell, byte[] value)
    {
        SetupRegistry(cell);
        IncrementChangePosition();
        _intraBlockCache[cell].Push(_currentPosition);
        _originalValues[cell] = value;
        _changes[_currentPosition] = new Change(ChangeType.JustCache, cell, value);
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
        throw new NotSupportedException("Verkle Trees does not support deletion of data from the tree");
    }
}
