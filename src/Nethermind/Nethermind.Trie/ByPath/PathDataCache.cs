// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Nethermind.Trie.ByPath;

public class NodeData
{
    public Keccak Keccak { get; }
    public byte[]? RLP { get; }

    public NodeData(byte[] data, Keccak keccak) { RLP = data; Keccak = keccak; }
}

internal class PathDataCacheInstance
{
    class StateId
    {
        static int _stateIdSeed = 0;
        public int Id { get; }
        public long? BlockNumber { get; set; }
        public Keccak? BlockStateRoot { get; set; }
        public StateId? ParentBlock { get; set; }
        public Keccak? ParentStateHash { get; set; }

        public StateId(long? blockNumber, Keccak? blockHash, Keccak parentStateRoot, StateId? parentBlock = null)
        {
            Id = Interlocked.Increment(ref _stateIdSeed);
            BlockNumber = blockNumber;
            BlockStateRoot = blockHash;
            ParentStateHash = parentStateRoot;
            ParentBlock = parentBlock;
        }

        private StateId(int id, long? blockNumber, Keccak blockHash, StateId? parentBlock = null)
        {
            Id = id;
            BlockNumber = blockNumber;
            BlockStateRoot = blockHash;
            ParentBlock = parentBlock;
        }

        public StateId Clone()
        {
            return new StateId(Id, BlockNumber, BlockStateRoot, ParentBlock);
        }
    }

    class PathDataAtState
    {
        public int StateId { get; }
        public NodeData Data { get; }
        public bool ShouldPersist { get; }

        public PathDataAtState(int stateId) { StateId = stateId; }
        public PathDataAtState(int stateId, NodeData data, bool shouldPersist) { StateId = stateId; Data = data; ShouldPersist = shouldPersist; }
    }

    class PathDataAtStateComparer : IComparer<PathDataAtState>
    {
        public int Compare(PathDataAtState? x, PathDataAtState? y)
        {
            return Comparer<int>.Default.Compare(x.StateId, y.StateId);
        }
    }

    class PathDataHistory
    {
        private readonly SortedSet<PathDataAtState> _nodes;
        private readonly ReaderWriterLockSlim _lock = new();

        public PathDataHistory()
        {
            _nodes = new SortedSet<PathDataAtState>(new PathDataAtStateComparer());
        }

        public PathDataHistory(IEnumerable<PathDataAtState> elements)
        {
            _nodes = new SortedSet<PathDataAtState>(new PathDataAtStateComparer());
            _nodes.AddRange(elements);
        }

        public int Count => _nodes.Count;

        public void Add(int stateId, NodeData data, bool shouldPersist)
        {
            try
            {
                _lock.EnterWriteLock();

                PathDataAtState nad = new(stateId, data, shouldPersist);
                _nodes.Remove(nad);
                _nodes.Add(nad);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public PathDataAtState? Get(Keccak keccak)
        {
            try
            {
                _lock.EnterReadLock();
                foreach (PathDataAtState nodeHist in _nodes)
                {
                    if (nodeHist.Data.Keccak == keccak)
                        return nodeHist;
                }
                return null;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public PathDataAtState? Get(Keccak keccak, int highestStateId)
        {
            try
            {
                _lock.EnterReadLock();

                if (_nodes.Count == 0) return null;
                if (highestStateId < _nodes.Min.StateId)
                    return null;

                foreach (PathDataAtState nodeHist in _nodes.GetViewBetween(_nodes.Min, new PathDataAtState(highestStateId)))
                {
                    if (nodeHist.Data.Keccak == keccak)
                        return nodeHist;
                }
                return null;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public PathDataAtState? GetLatest()
        {
            try
            {
                _lock.EnterReadLock();
                return _nodes.Max;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public PathDataAtState? GetLatestUntil(int stateId)
        {
            try
            {
                _lock.EnterReadLock();

                if (_nodes.Count == 0) return null;
                if (stateId < _nodes.Min.StateId)
                    return null;

                return _nodes.GetViewBetween(_nodes.Min, new PathDataAtState(stateId)).Max;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
        public void ClearUntil(int stateId)
        {
            try
            {
                _lock.EnterWriteLock();

                if (_nodes.Count == 0 || stateId < _nodes.Min.StateId)
                    return;

                SortedSet<PathDataAtState> viewUntilBlock = _nodes.GetViewBetween(_nodes.Min, new PathDataAtState(stateId));
                viewUntilBlock.Clear();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public PathDataHistory? SplitAt(int stateId)
        {
            try
            {
                _lock.EnterWriteLock();

                if (_nodes.Count == 0 || stateId > _nodes.Max.StateId)
                    return null;

                SortedSet<PathDataAtState> viewFromBlock = _nodes.GetViewBetween(new PathDataAtState(stateId), _nodes.Max);
                PathDataAtState[] copy = new PathDataAtState[viewFromBlock.Count];
                viewFromBlock.CopyTo(copy);
                PathDataHistory newHistory = new(copy);
                viewFromBlock.Clear();
                return newHistory;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public void Merge(PathDataHistory history)
        {
            try
            {
                _lock.EnterWriteLock();
                _nodes.UnionWith(history._nodes);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }
    }

    public PathDataCacheInstance(ITrieStore trieStore, ILogger? logger)
    {
        _trieStore = trieStore;
        _logger = logger;
        _branches = new ConcurrentBag<PathDataCacheInstance>();
        _removedPrefixes = new SpanConcurrentDictionary<byte, List<int>>(Bytes.SpanNibbleEqualityComparer);
    }

    private PathDataCacheInstance(ITrieStore trieStore, ILogger? logger, StateId lastState, PathDataCacheInstance? parent, StateId? parentStateId, IEnumerable<PathDataCacheInstance> branches = null)
    {
        _lastState = lastState;
        _trieStore = trieStore;
        _logger = logger;
        _branches = new ConcurrentBag<PathDataCacheInstance>();
        _removedPrefixes = new SpanConcurrentDictionary<byte, List<int>>(Bytes.SpanNibbleEqualityComparer);
        _parentInstance = parent;
        if (branches is not null)
        {
            foreach (var b in branches)
                _branches.Add(b);
        }
        _parentStateId = parentStateId;
    }

    private StateId _lastState;

    private ConcurrentBag<PathDataCacheInstance> _branches;

    private PathDataCacheInstance? _parentInstance;
    private StateId _parentStateId;

    private SpanConcurrentDictionary<byte, PathDataHistory> _historyByPath = new(Bytes.SpanNibbleEqualityComparer);
    private SpanConcurrentDictionary<byte, List<int>> _removedPrefixes;

    private readonly ITrieStore _trieStore;
    private readonly ILogger _logger;
    private bool _isDirty;
    public bool IsDirty => _isDirty;
    public int PrefixLength { get; set; } = 66;

    public bool IsOpened => _lastState.BlockStateRoot is null;
    public bool IsEmpty => _historyByPath.Count == 0 && _removedPrefixes.Count == 0;

    public Keccak LatestParentStateRootHash => _lastState?.ParentStateHash;
    public bool SingleBlockOnly => _lastState?.ParentBlock is null;

    public PathDataCacheInstance? CloseState(long blockNumber, Keccak stateRootHash)
    {
        if (IsOpened)
        {
            _isDirty = false;
            if (_lastState.ParentBlock?.BlockNumber == blockNumber && _lastState.ParentBlock?.BlockStateRoot == stateRootHash)
            {
                _lastState = _lastState.ParentBlock;
            }
            else
            {
                //reorg inside instance
                if (blockNumber < _lastState.BlockNumber)
                {
                    Keccak searchedParentHash = _lastState.ParentStateHash;
                    StateId st = _lastState;
                    while (st is not null)
                    {
                        if (st.ParentStateHash == searchedParentHash && st.BlockNumber <= blockNumber)
                            break;
                        st = st.ParentBlock;
                    }
                    if (st?.BlockStateRoot == stateRootHash)
                    {
                        _lastState = _lastState.ParentBlock;
                        return null;
                    }

                    PathDataCacheInstance parentCacheInstance = st?.ParentBlock is null ? _parentInstance : this;

                    //copy the new block data one into seperate instance
                    StateId clonedState = _lastState.Clone();
                    clonedState.BlockStateRoot = stateRootHash;
                    clonedState.BlockNumber = blockNumber;
                    clonedState.ParentBlock = null;
                    clonedState.ParentStateHash = _lastState.ParentStateHash;

                    PathDataCacheInstance newBranch = new(_trieStore, _logger, clonedState, parentCacheInstance, st?.ParentBlock);

                    foreach (KeyValuePair<byte[], PathDataHistory> histEntry in _historyByPath)
                    {
                        PathDataHistory? newHist = histEntry.Value.SplitAt(_lastState.Id);
                        if (newHist is not null)
                            newBranch.Add(histEntry.Key, newHist);
                    }

                    _lastState = _lastState.ParentBlock;

                    //create new instance from existing
                    if (st?.ParentBlock is not null)
                    {
                        PathDataCacheInstance copyOfExisting = new(_trieStore, _logger, _lastState, parentCacheInstance, st?.ParentBlock, _branches);

                        foreach (KeyValuePair<byte[], PathDataHistory> histEntry in _historyByPath)
                        {
                            PathDataHistory? newHist = histEntry.Value.SplitAt(st.Id);
                            if (newHist is not null)
                                copyOfExisting.Add(histEntry.Key, newHist);
                        }
                        _branches.Clear();
                        _branches.Add(copyOfExisting);
                        _branches.Add(newBranch);

                        _lastState = st.ParentBlock;
                        st.ParentBlock = null;
                    }
                    else
                    {
                        return newBranch;
                    }
                }
                else
                {
                    _lastState.BlockStateRoot = stateRootHash;
                    _lastState.BlockNumber = blockNumber;
                }
            }
        }
        return null;
    }

    public void RollbackOpenedState()
    {
        if (IsOpened)
        {
            _lastState = _lastState.ParentBlock;
        }
    }

    public PathDataCacheInstance? GetCacheInstanceForParent(Keccak parentStateRoot)
    {
        if (_lastState is null)
        {
            _lastState = new StateId(null, null, parentStateRoot);
            return this;
        }

        foreach (PathDataCacheInstance branch in _branches)
        {
            PathDataCacheInstance innerCache = branch.GetCacheInstanceForParent(parentStateRoot);
            if (innerCache is not null)
                return innerCache;
        }

        StateId stateId = FindState(parentStateRoot);
        if (stateId is not null)
        {
            if (stateId == _lastState)
            {
                _lastState = new StateId(_lastState.BlockNumber + 1, null, parentStateRoot, _lastState);
                return this;
            }
            else
            {
                return new PathDataCacheInstance(_trieStore, _logger, new StateId(stateId.BlockNumber + 1, null, parentStateRoot), this, stateId);
            }
        }

        return null;
    }

    public PathDataCacheInstance? GetCacheInstanceForPersisted(Keccak parentStateRoot)
    {
        StateId firstState = GetFirstState();
        if (firstState.ParentStateHash == parentStateRoot)
            return new PathDataCacheInstance(_trieStore, _logger, new StateId(firstState.BlockNumber, null, parentStateRoot), null, null);
        return null;
    }


    public void PrepareForCommit()
    {
        _isDirty = true;
    }

    private StateId? SplitAt(Keccak stateRoot, long? highestBlockNumber = null)
    {
        //create a new branch
        StateId? currState = FindStateWithParent(stateRoot, highestBlockNumber);
        if (currState is not null)
        {
            PathDataCacheInstance newBranchExisting = new(_trieStore, _logger, _lastState, this, currState.ParentBlock, _branches);

            foreach (KeyValuePair<byte[], PathDataHistory> histEntry in _historyByPath)
            {
                PathDataHistory? newHist = histEntry.Value.SplitAt(currState.Id);
                if (newHist is not null)
                    newBranchExisting.Add(histEntry.Key, newHist);
            }
            _branches.Clear();
            _branches.Add(newBranchExisting);

            _lastState = currState.ParentBlock;
            currState.ParentBlock = null;
        }
        return currState;
    }

    public bool MergeWith(PathDataCacheInstance newInstance)
    {
        if (_lastState.BlockStateRoot == newInstance._lastState.ParentStateHash)
        {
            if (newInstance._lastState.BlockNumber == _lastState.BlockNumber &&
                newInstance._lastState.BlockStateRoot == _lastState.BlockStateRoot)
            {
                //same block - nothing to do
                return true;
            }

            if (newInstance._lastState.BlockNumber > _lastState.BlockNumber)
            {
                newInstance._lastState.ParentBlock = _lastState;
                _lastState = newInstance._lastState;

                foreach (KeyValuePair<byte[], PathDataHistory> nodeVersion in newInstance._historyByPath)
                {
                    if (_historyByPath.TryGetValue(nodeVersion.Key, out PathDataHistory pathDataHistory))
                        pathDataHistory.Merge(nodeVersion.Value);
                    else
                        _historyByPath[nodeVersion.Key] = nodeVersion.Value;
                }

                foreach (var kvp in newInstance._removedPrefixes)
                {
                    if (_removedPrefixes.TryGetValue(kvp.Key, out List<int> stateIds))
                        stateIds.AddRange(kvp.Value);
                    else
                        _removedPrefixes[kvp.Key] = kvp.Value;
                }
                return true;
            }
        }

        foreach (var branch in _branches)
        {
            if (branch.MergeWith(newInstance))
                return true;
        }
        return false;
    }

    public bool MergeToParent()
    {
        if (_parentInstance == null)
            return false;

        bool merged = _parentInstance.MergeWith(this);

        if (!merged)
        {
            if (_parentInstance.SplitAt(_lastState.ParentStateHash, _lastState.BlockNumber + 1) is not null)
            {
                _parentInstance._branches.Add(this);
                return true;
            }
            return false;
        }
        return merged;
    }

    public void AddNodeData(long blockNuber, TrieNode node)
    {
        if (!IsOpened)
            throw new ArgumentException("Can't add node to closed cache instance");

        if (_logger.IsTrace) _logger.Trace($"Adding node {node.PathToNode.ToHexString()} / {node.FullPath.ToHexString()} with Keccak: {node.Keccak} at block {blockNuber}");

        NodeData nd = new(node.FullRlp.Array, node.Keccak);
        if (node.IsLeaf)
        {
            Span<byte> pathToNode = node.PathToNode;
            if (node.StoreNibblePathPrefix.Length > 0)
                pathToNode = Bytes.Concat(node.StoreNibblePathPrefix, node.PathToNode);

            PathDataHistory leafPointerHist = GetHistoryForPath(pathToNode);
            leafPointerHist.Add(_lastState.Id, nd, false);
        }
        PathDataHistory history = GetHistoryForPath(node.FullPath);
        history.Add(_lastState.Id, nd, true);
    }

    public NodeData? GetNodeDataAtRoot(Keccak? rootHash, Span<byte> path)
    {
        rootHash ??= _lastState.BlockStateRoot;
        StateId localState = FindState(rootHash);
        if (localState is not null)
        {
            PathDataAtState? latestDataHeld = null;
            if (_historyByPath.TryGetValue(path, out PathDataHistory pathHistory))
            {
                latestDataHeld = pathHistory.GetLatestUntil(localState.Id);
            }

            if (WasRemovedAfter(path, latestDataHeld?.StateId ?? -1))
            {
                Pruning.Metrics.LoadedFromCacheNodesCount++;
                return new NodeData(null, Keccak.OfAnEmptySequenceRlp);
            }

            if (latestDataHeld is not null)
            {
                Pruning.Metrics.LoadedFromCacheNodesCount++;
                return latestDataHeld.Data;
            }
        }
        return _parentInstance?.GetNodeDataAtRoot(_parentStateId?.BlockStateRoot, path);
    }

    public NodeData? GetNodeData(Span<byte> path, Keccak? hash)
    {
        NodeData? data = null;
        if (_historyByPath.TryGetValue(path, out PathDataHistory history))
            data = history.Get(hash)?.Data;

        if (data is null)
        {
            foreach (PathDataCacheInstance branch in _branches)
            {
                data = branch.GetNodeData(path, hash);
                if (data is not null)
                    break;
            }
        }
        return data;
    }

    private NodeData? GetNodeData(Span<byte> path, Keccak? hash, StateId? highestStateId)
    {
        NodeData? data = null;
        if (_historyByPath.TryGetValue(path, out PathDataHistory history))
            data = highestStateId is not null ? (history.Get(hash, highestStateId.Id)?.Data) : (history.Get(hash)?.Data);

        return data is null ? (_parentInstance?.GetNodeData(path, hash, highestStateId)) : data;
    }

    public bool PersistUntilBlock(long blockNumber, Keccak rootHash, IBatch? batch = null)
    {
        Stack<PathDataCacheInstance> branches = new Stack<PathDataCacheInstance>();
        GetBranchesToProcess(blockNumber, rootHash, branches, true, out StateId? latestState);

        if (branches.Count == 0)
            return false;

        while (branches.Count > 0)
        {
            PathDataCacheInstance branch = branches.Pop();
            branch.PersistUntilBlockInner(latestState, batch);
        }

        PruneUntil(blockNumber, rootHash);
        return true;
    }

    private void PersistUntilBlockInner(StateId? stateId, IBatch? batch = null)
    {
        ProcessDestroyed(stateId);

        List<TrieNode> toPersist = new();
        foreach (KeyValuePair<byte[], PathDataHistory> nodeVersion in _historyByPath)
        {
            PathDataAtState? nodeData = nodeVersion.Value.GetLatestUntil(stateId.Id);
            if (nodeData?.ShouldPersist == true)
            {
                NodeData data = nodeData.Data;
                TrieNode node;
                if (nodeVersion.Key.Length >= 66)
                {
                    byte[] prefix = nodeVersion.Key.Slice(0, 66);
                    node = new(NodeType.Unknown, nodeVersion.Key.Slice(66), data.Keccak, data.RLP);
                    node.StoreNibblePathPrefix = prefix;
                }
                else
                {
                    node = new(NodeType.Unknown, nodeVersion.Key, data.Keccak, data.RLP);
                }

                if (data.RLP is not null)
                {
                    node.ResolveNode(_trieStore);
                    node.ResolveKey(_trieStore, nodeVersion.Key.Length == 0);
                }
                if (data.RLP is null) toPersist.Insert(0, node); else toPersist.Add(node);
            }
        }

        foreach (TrieNode node in toPersist)
        {
            _trieStore.SaveNodeDirectly(stateId.BlockNumber.Value, node, batch);
            if (_logger.IsTrace) _logger.Trace($"Persising node {node.PathToNode.ToHexString()} / {node.FullPath.ToHexString()} with Keccak: {node.Keccak} at block {stateId.BlockNumber} / {stateId.BlockStateRoot} Value: {node.FullRlp.ToArray()?.ToHexString()}");
        }
    }

    public bool PruneUntil(long blockNumber, Keccak rootHash)
    {
        Stack<PathDataCacheInstance> branches = new Stack<PathDataCacheInstance>();
        GetBranchesToProcess(blockNumber, rootHash, branches, false, out StateId? latestState);

        if (branches.Count == 0)
            return false;

        while (branches.Count > 1)
            branches.Pop();

        PathDataCacheInstance branch = branches.Pop();
        branch.PruneUntilInner(latestState);

        if (branch != this)
        {
            _branches = branch._branches;
            _historyByPath = branch._historyByPath;
            _lastState = branch._lastState;
        }

        return true;
    }

    private void PruneUntilInner(StateId stateId)
    {
        List<byte[]> removedPaths = new();
        foreach (KeyValuePair<byte[], PathDataHistory> nodeVersion in _historyByPath)
        {
            nodeVersion.Value.ClearUntil(stateId.Id);
            if (nodeVersion.Value.Count == 0)
                removedPaths.Add(nodeVersion.Key);
        }

        foreach (byte[] path in removedPaths)
            _historyByPath.Remove(path, out _);

        removedPaths.Clear();
        List<byte[]> paths = _removedPrefixes.Where(kvp => kvp.Value.Exists(st => st <= stateId.Id)).Select(kvp => kvp.Key).ToList();
        
        foreach (byte[] path in paths)
        {
            List<int> states = _removedPrefixes[path];
            for (int i = states.Count - 1; i >= 0; i--)
            {
                if (states[i] <= stateId.Id)
                    states.RemoveAt(i);
            }
            if (states.Count == 0)
                removedPaths.Add(path);
        }
        foreach (byte[] path in removedPaths)
            _removedPrefixes.Remove(path, out _);

        StateId? childOfPersisted = FindStateWithParent(stateId.BlockStateRoot, stateId.BlockNumber + 1);
        if (childOfPersisted is not null)
            childOfPersisted.ParentBlock = null;
        else
            _lastState = null;

        _branches.Clear();
    }
    private void ProcessDestroyed(StateId stateId)
    {
        List<byte[]> paths = _removedPrefixes.Where(kvp => kvp.Value.Exists(st => st <= stateId.Id)).Select(kvp => kvp.Key).ToList();

        foreach (byte[] path in paths)
        {
            (byte[] startKey, byte[] endKey) = TrieStoreByPath.GetDeleteKeyFromNibblePrefix(path);
            _trieStore.DeleteByRange(startKey, endKey);
        }
    }

    private PathDataHistory GetHistoryForPath(Span<byte> path)
    {
        if (!_historyByPath.TryGetValue(path, out PathDataHistory history))
        {
            history = new PathDataHistory();
            _historyByPath[path] = history;
        }

        return history;
    }

    private StateId? FindState(Keccak rootHash, long blockNumber = -1)
    {
        StateId stateId = _lastState;
        while (stateId is not null)
        {
            if (stateId.BlockStateRoot == rootHash && (stateId.BlockNumber == blockNumber || blockNumber == -1))
                return stateId;
            stateId = stateId.ParentBlock;
        }
        return null;
    }

    private StateId? GetFirstState()
    {
        StateId stateId = _lastState;
        while (stateId.ParentBlock is not null)
        {
            stateId = stateId.ParentBlock;
        }
        return stateId;
    }

    private bool GetBranchesToProcess(long blockNumber, Keccak stateRoot, Stack<PathDataCacheInstance> branches, bool filterDetached, out StateId? latestState)
    {
        StateId? localState = FindState(stateRoot, blockNumber);
        if (localState is not null)
        {
            branches.Push(this);
            latestState = localState;
            return true;
        }

        foreach (PathDataCacheInstance branchCache in _branches)
        {
            if (branchCache.GetBranchesToProcess(blockNumber, stateRoot, branches, filterDetached, out latestState))
            {
                branches.Push(this);
                return true;
            }
        }
        latestState = null;
        return false;
    }

    private StateId? FindStateIncludingBranches(Keccak rootHash, long blockNumber = -1)
    {
        foreach (PathDataCacheInstance branchCache in _branches)
        {
            StateId? stateInBranch = branchCache.FindStateIncludingBranches(rootHash, blockNumber);
            if (stateInBranch is not null)
                return stateInBranch;
        }

        StateId stateId = _lastState;
        while (stateId is not null)
        {
            if (stateId.BlockStateRoot == rootHash && (stateId.BlockNumber == blockNumber || blockNumber == -1))
                return stateId;
            stateId = stateId.ParentBlock;
        }
        return null;
    }

    private StateId? FindStateWithParent(Keccak rootHash, long? highestBlockNumber = null)
    {
        StateId stateId = _lastState;
        while (stateId.ParentBlock is not null)
        {
            if (stateId.ParentBlock.BlockStateRoot == rootHash && (highestBlockNumber is null || stateId.BlockNumber <= highestBlockNumber))
                return stateId;
            stateId = stateId.ParentBlock;
        }
        return null;
    }

    private void Add(Span<byte> path, PathDataHistory history)
    {
        _historyByPath[path] = history;
    }

    public void AddRemovedPrefix(long blockNumber, ReadOnlySpan<byte> keyPrefix)
    {
        if (!_removedPrefixes.TryGetValue(keyPrefix, out List<int> destroyedAtStates))
        {
            destroyedAtStates = new List<int>();
            _removedPrefixes[keyPrefix] = destroyedAtStates;
        }
        destroyedAtStates.Add(_lastState.Id);
    }

    private bool WasRemovedAfter(Span<byte> path, int stateId)
    {
        if (path.Length >= PrefixLength && _removedPrefixes.TryGetValue(path[0..PrefixLength], out List<int> deletedAtStates))
        {
            for (int i = 0; i < deletedAtStates.Count; i++)
                if (deletedAtStates[i] > stateId) return true;
        }
        return false;
    }

    public bool HasSameState(PathDataCacheInstance newInstance)
    {
        StateId st = _lastState;
        while (st is not null)
        {
            if (newInstance._lastState.BlockNumber == st.BlockNumber && newInstance._lastState.BlockStateRoot == st.BlockStateRoot)
                return true;

            st = st.ParentBlock;
        }
        return false;
    }

    public void LogCacheContents(int level)
    {
        StateId stateId = _lastState;
        Stack<StateId> stack = new Stack<StateId>();
        while (stateId is not null)
        {
            stack.Push(stateId);
            stateId = stateId.ParentBlock;
        }

        StringBuilder sb = new StringBuilder();
        sb.Append(string.Concat(Enumerable.Repeat("\t", level)));
        while (stack.Count > 0)
        {
            StateId st = stack.Pop();
            sb.Append($"[B {st.BlockNumber} | H {st.BlockStateRoot} | I {st.Id}] -> ");
        }

        _logger.Trace(sb.ToString());

        foreach (PathDataCacheInstance branch in _branches)
            branch.LogCacheContents(level + 1);
    }
}

public class PathDataCache : IPathDataCache
{
    public PathDataCache(ITrieStore trieStore, ILogManager? logManager)
    {
        _trieStore = trieStore;
        _logger = logManager?.GetClassLogger<TrieNodePathCache>() ?? throw new ArgumentNullException(nameof(logManager));
        _mains = new List<PathDataCacheInstance>
        {
            new PathDataCacheInstance(_trieStore, _logger)
        };
    }

    private readonly ITrieStore _trieStore;
    private readonly ILogger _logger;

    private List<PathDataCacheInstance> _mains;
    private PathDataCacheInstance _openedInstance;

    public int PrefixLength { get; set; } = 66;

    public void OpenContext(Keccak parentStateRoot)
    {
        if (_openedInstance is not null)
        {
            if (_openedInstance.LatestParentStateRootHash == parentStateRoot)
                return;

            if (!_openedInstance.IsDirty)
            {
                _openedInstance.RollbackOpenedState();
                _openedInstance = null;
            }
            else if (_openedInstance.IsEmpty)
            {
                _openedInstance = null;
            }
            else
                throw new ArgumentException("Already having instance opened");
        }

        _openedInstance = GetCacheInstanceForState(parentStateRoot);
    }

    public void CloseContext(long blockNumber, Keccak newStatRoot)
    {
        if (_openedInstance is null)
        {
            foreach (var mainInstance in _mains)
            {
                if (mainInstance.IsEmpty)
                {
                    mainInstance.GetCacheInstanceForParent(Keccak.EmptyTreeHash);
                    mainInstance.CloseState(blockNumber, newStatRoot);
                }
            }
            return;
        }

        PathDataCacheInstance? newInstance = _openedInstance.CloseState(blockNumber, newStatRoot);
        newInstance ??= _openedInstance;

        if (newInstance?.SingleBlockOnly == true)
        {
            if (!newInstance.MergeToParent() && !StateAlreadyInMain(newInstance))
                _mains.Add(newInstance);
        }

        _openedInstance = null;

        LogCache("Cache at end of CloseContext");
        
    }

    public void AddNodeData(long blockNuber, TrieNode node)
    {
        if (_openedInstance is null)
        {
            if (_mains.Count == 1 && _mains[0].IsEmpty)
            {
                _openedInstance = _mains[0].GetCacheInstanceForParent(Keccak.EmptyTreeHash);
            }
            else
                throw new ArgumentException("No cache instance opened");
        }

        _openedInstance.PrepareForCommit();
        _openedInstance.AddNodeData(blockNuber, node);
    }

    public NodeData? GetNodeDataAtRoot(Keccak? rootHash, Span<byte> path)
    {
        if (rootHash is not null)
        {
            if (_openedInstance?.LatestParentStateRootHash != rootHash)
            {
                PathDataCacheInstance tmpInstance = GetCacheInstanceForState(rootHash);
                if (tmpInstance is null)
                    if (_logger.IsTrace) _logger.Trace($"Root hash {rootHash} not available in commidated cache");
                return tmpInstance?.GetNodeDataAtRoot(rootHash, path);
            }
        }
        NodeData? data = _openedInstance?.GetNodeDataAtRoot(rootHash, path);
        return data;
    }

    public NodeData? GetNodeData(Span<byte> path, Keccak? hash)
    {
        NodeData? data = null;
        if (_openedInstance is not null)
        {
            data = _openedInstance.GetNodeData(path, hash);
        }

        if (data is null)
        {
            foreach (PathDataCacheInstance instance in _mains)
            {
                data = instance.GetNodeData(path, hash);
                if (data is not null)
                    break;
            }
        }
        return data;
    }

    public bool PersistUntilBlock(long blockNumber, Keccak rootHash, IBatch? batch = null)
    {
        for (int i = _mains.Count - 1; i >= 0; i--)
        {
            if (!_mains[i].PersistUntilBlock(blockNumber, rootHash, batch))
                _mains.RemoveAt(i);
        }

        if (_mains.Count == 0)
            _mains.Add(new PathDataCacheInstance(_trieStore, _logger));

        LogCache("After persisting");
        return true;
    }

    public void AddRemovedPrefix(long blockNumber, ReadOnlySpan<byte> keyPrefix)
    {
        if (_openedInstance is null)
            throw new ArgumentException("No cache instance opened");

        _openedInstance.AddRemovedPrefix(blockNumber, keyPrefix);
    }

    private PathDataCacheInstance? GetCacheInstanceForState(Keccak parentStateRoot)
    {
        PathDataCacheInstance newInstance = null;
        foreach (var mainInstance in _mains)
        {
            if ((newInstance = mainInstance.GetCacheInstanceForParent(parentStateRoot)) is not null)
                break;
        }
        if (newInstance is null)
            newInstance = _mains[0].GetCacheInstanceForPersisted(parentStateRoot);

        return newInstance;
    }

    private bool StateAlreadyInMain(PathDataCacheInstance newInstance)
    {
        bool same = false;
        foreach (var mainInstance in _mains)
        {
            if (same = mainInstance.HasSameState(newInstance) == true)
                break;
        }
        return same;
    }

    public void LogCache(string msg)
    {
        if (!_logger.IsTrace)
            return;

        _logger.Trace(msg);

        foreach (var mainInstance in _mains)
            mainInstance.LogCacheContents(0);
    }
}
