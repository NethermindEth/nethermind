// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie.ByPath;

public class NodeData
{
    public Hash256 Keccak { get; }
    public byte[]? RLP { get; }

    public NodeData(byte[] data, Hash256 keccak) { RLP = data; Keccak = keccak; }

    public TrieNode ToTrieNode(Span<byte> path)
    {
        TrieNode trieNode = new(NodeType.Unknown, path.ToArray(), Keccak, RLP);
        trieNode.ResolveNode(NullTrieNodeResolver.Instance);
        return trieNode;
    }
}
internal class PathDataCacheInstance
{
    class StateId
    {
        static int _stateIdSeed = 0;
        public int Id { get; }
        public long? BlockNumber { get; set; }
        public Hash256? BlockStateRoot { get; set; }
        public StateId? ParentBlock { get; set; }
        public Hash256? ParentStateHash { get; set; }

        public StateId(long? blockNumber, Hash256? blockHash, Hash256 parentStateRoot, StateId? parentBlock = null)
        {
            Id = Interlocked.Increment(ref _stateIdSeed);
            BlockNumber = blockNumber;
            BlockStateRoot = blockHash;
            ParentStateHash = parentStateRoot;
            ParentBlock = parentBlock;
        }

        private StateId(int id, long? blockNumber, Hash256 blockHash, StateId? parentBlock = null)
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

            PathDataAtState nad = new(stateId, data, shouldPersist);
            _nodes.Remove(nad);
            _nodes.Add(nad);
        }

        public PathDataAtState? Get(Hash256 keccak)
        {
            foreach (PathDataAtState nodeHist in _nodes)
            {
                if (nodeHist.Data.Keccak == keccak)
                    return nodeHist;
            }
            return null;
        }

        public PathDataAtState? Get(Hash256 keccak, int highestStateId)
        {
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

        public PathDataAtState? GetLatest() => _nodes.Max;

        public PathDataAtState? GetLatestUntil(int stateId)
        {
            if (_nodes.Count == 0) return null;
            if (stateId < _nodes.Min.StateId)
                return null;

            return _nodes.GetViewBetween(_nodes.Min, new PathDataAtState(stateId)).Max;
        }

        public void ClearUntil(int stateId)
        {
            if (_nodes.Count == 0 || stateId < _nodes.Min.StateId)
                return;

            SortedSet<PathDataAtState> viewUntilBlock = _nodes.GetViewBetween(_nodes.Min, new PathDataAtState(stateId));
            viewUntilBlock.Clear();
        }

        public PathDataHistory? SplitAt(int stateId)
        {
            if (_nodes.Count == 0 || stateId > _nodes.Max.StateId)
                return null;

            SortedSet<PathDataAtState> viewFromBlock = _nodes.GetViewBetween(new PathDataAtState(stateId), _nodes.Max);
            PathDataAtState[] copy = new PathDataAtState[viewFromBlock.Count];
            viewFromBlock.CopyTo(copy);
            PathDataHistory newHistory = new(copy);
            viewFromBlock.Clear();
            return newHistory;
        }
    }

    public PathDataCacheInstance(ITrieStore trieStore, ILogger? logger, int prefixLength = 66)
    {
        _trieStore = trieStore;
        _logger = logger;
        _branches = new List<PathDataCacheInstance>();
        _removedPrefixes = new SpanDictionary<byte, List<int>>(Bytes.SpanNibbleEqualityComparer);
        _prefixLength = prefixLength;
    }

    private PathDataCacheInstance(ITrieStore trieStore, ILogger? logger, StateId lastState, PathDataCacheInstance? parent, int prefixLength = 66, IEnumerable<PathDataCacheInstance> branches = null) :
        this(trieStore, logger, prefixLength)
    {
        _lastState = lastState;
        _parentInstance = parent;
        if (branches is not null)
            _branches.AddRange(branches);
    }

    private StateId _lastState;

    private List<PathDataCacheInstance> _branches;

    private PathDataCacheInstance? _parentInstance;

    private SpanDictionary<byte, PathDataHistory> _historyByPath = new(Bytes.SpanNibbleEqualityComparer);
    private SpanDictionary<byte, List<int>> _removedPrefixes;

    private readonly ITrieStore _trieStore;
    private readonly ILogger _logger;
    private bool _isDirty;
    private int _prefixLength = 66;

    public bool IsOpened => _lastState?.BlockStateRoot is null;
    public bool IsEmpty => _historyByPath.Count == 0 && _removedPrefixes.Count == 0;

    public Hash256 LatestParentStateRootHash => _lastState?.ParentStateHash;

    public bool SingleBlockOnly => _lastState?.ParentBlock is null;

    public void CloseState(Hash256 newStateRootHash)
    {
        if (IsOpened)
        {
            _isDirty = false;
            _lastState.BlockStateRoot = newStateRootHash;
        }
    }

    public void RollbackOpenedState()
    {
        if (IsOpened && !_isDirty)
        {
            _lastState = _lastState.ParentBlock;
        }
    }

    public PathDataCacheInstance? GetCacheInstanceForParent(Hash256 parentStateRoot, long blockNumber, out bool isParallelToExistingBranch)
    {
        isParallelToExistingBranch = false;
        foreach (PathDataCacheInstance branch in _branches)
        {
            PathDataCacheInstance innerCache = branch.GetCacheInstanceForParent(parentStateRoot, blockNumber, out isParallelToExistingBranch);
            if (innerCache is not null)
            {
                if (isParallelToExistingBranch)
                    _branches.Add(innerCache);
                return innerCache;
            }
        }

        if (_lastState is null)
        {
            _lastState = new StateId(blockNumber, null, parentStateRoot);
            return this;
        }

        if (parentStateRoot == _lastState.BlockStateRoot && blockNumber > _lastState.BlockNumber)
        {
            _lastState = new StateId(blockNumber, null, parentStateRoot, _lastState);
            return this;
        }

        StateId? splitState = _lastState;
        while (splitState is not null)
        {
            if (splitState.ParentStateHash == parentStateRoot && splitState.BlockNumber <= blockNumber)
                break;
            splitState = splitState.ParentBlock;
        }

        if (splitState is not null)
        {
            if (splitState.ParentBlock is not null || (splitState.ParentBlock is null && _parentInstance is null))
            {
                PathDataCacheInstance newBranch = new(_trieStore, _logger, new StateId(blockNumber, null, parentStateRoot), this, _prefixLength);
                SplitAt(splitState);
                _branches.Add(newBranch);
                return newBranch;
            }
            isParallelToExistingBranch = true;
            return new(_trieStore, _logger, new StateId(blockNumber, null, parentStateRoot), _parentInstance, _prefixLength);
        }

        return null;
    }

    public PathDataCacheInstance? FindCacheInstanceForStateRoot(Hash256 stateRoot)
    {
        StateId stateId = FindState(stateRoot);
        if (stateId is not null)
            return this;

        foreach (PathDataCacheInstance branch in _branches)
        {
            PathDataCacheInstance innerCache = branch.FindCacheInstanceForStateRoot(stateRoot);
            if (innerCache is not null)
                return innerCache;
        }
        return null;
    }

    public void AddRemovedPrefix(long blockNumber, ReadOnlySpan<byte> keyPrefix)
    {
        if (!_removedPrefixes.TryGetValue(keyPrefix, out List<int> destroyedAtStates))
        {
            destroyedAtStates = new List<int>();
            _removedPrefixes[keyPrefix] = destroyedAtStates;
        }
        destroyedAtStates.Add(_lastState.Id);

        if (_logger.IsTrace) _logger.Trace($"Added prefix for removal {keyPrefix.ToHexString()} at block {_lastState.BlockNumber} / requested {blockNumber}");
    }

    public void AddNodeData(long blockNuber, TrieNode node)
    {
        if (!IsOpened)
            throw new ArgumentException("Can't add node to closed cache instance");

        if (_logger.IsTrace) _logger.Trace($"Adding node {node.PathToNode.ToHexString()} / {node.FullPath.ToHexString()} with Hash256: {node.Keccak} at block {blockNuber}");

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
        _isDirty = true;
    }

    public NodeData? GetNodeDataAtRoot(Hash256? rootHash, Span<byte> path)
    {
        rootHash ??= _lastState?.BlockStateRoot;
        StateId localState = FindState(rootHash);
        if (localState is not null)
        {
            PathDataAtState? latestDataHeld = null;
            if (_historyByPath.TryGetValue(path, out PathDataHistory pathHistory))
            {
                latestDataHeld = pathHistory.GetLatestUntil(localState.Id);
            }

            if (WasRemovedBetween(path, latestDataHeld?.StateId ?? -1, localState.Id))
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
        return _parentInstance?.GetNodeDataAtRoot(null, path);
    }

    public NodeData? GetNodeData(Span<byte> path, Hash256? hash)
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

    public bool PersistUntilBlock(long blockNumber, Hash256 rootHash, IColumnsWriteBatch<StateColumns>? batch = null)
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

    public bool PruneUntil(long blockNumber, Hash256 rootHash)
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
            _removedPrefixes = branch._removedPrefixes;
            _lastState = branch._lastState;
        }

        return true;
    }

    private void PersistUntilBlockInner(StateId? stateId, IColumnsWriteBatch<StateColumns>? batch = null)
    {
        if (_logger.IsTrace)
            _logger.Trace($"Persisting cache instance with latest state {_lastState?.BlockNumber} / {_lastState?.BlockStateRoot} until state {stateId?.BlockNumber} / {stateId?.BlockStateRoot}");

        ProcessDestroyed(stateId);

        List<Tuple<byte[], int, byte[]>> toPersist = new();
        foreach (KeyValuePair<byte[], PathDataHistory> nodeVersion in _historyByPath)
        {
            PathDataAtState? nodeData = nodeVersion.Value.GetLatestUntil(stateId.Id);
            if (nodeData?.ShouldPersist == true)
            {
                if (WasRemovedBetween(nodeVersion.Key, nodeData.StateId, stateId?.Id ?? 0))
                    continue;

                NodeData data = nodeData.Data;

                int leafPathToNodeLength = -1;
                if (data.RLP is null)
                {
                    toPersist.Insert(0, Tuple.Create(nodeVersion.Key, leafPathToNodeLength, data.RLP));
                }
                else
                {
                    //part of TrieNode ResolveNode to get path to node for leaves - should this just be a part of NodeData ?
                    RlpStream rlpStream = data.RLP.AsRlpStream();
                    rlpStream.ReadSequenceLength();
                    // micro optimization to prevent searches beyond 3 items for branches (search up to three)
                    int numberOfItems = rlpStream.PeekNumberOfItemsRemaining(null, 3);
                    if (numberOfItems == 2)
                    {
                        (byte[] key, bool isLeaf) = HexPrefix.FromBytes(rlpStream.DecodeByteArraySpan());
                        if (isLeaf)
                            leafPathToNodeLength = 64 - key.Length;
                    }
                    toPersist.Add(Tuple.Create(nodeVersion.Key, leafPathToNodeLength, data.RLP));
                }

                if (_logger.IsTrace)
                {
                    //StateId? originalState = FindState(nodeData.StateId);
                    //Span<byte> pathToNode = Array.Empty<byte>();
                    //if (leafPathToNodeLength >= 0)
                    //{
                    //    pathToNode = nodeVersion.Key.Length >= 66 ? nodeVersion.Key[..(leafPathToNodeLength + 66)] : nodeVersion.Key[..(leafPathToNodeLength)];
                    //}
                    //_logger.Trace($"Persising node {pathToNode.ToHexString()} / {nodeVersion.Key.ToHexString()} with Hash256: {data.Hash256} from block {originalState?.BlockNumber} / {stateId.BlockStateRoot} Value: {data.RLP.ToArray()?.ToHexString()}");
                }
            }
        }

        foreach (var data in toPersist)
        {
            _trieStore.PersistNodeData(data.Item1, data.Item2, data.Item3, batch);
        }
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
    }

    private void ProcessDestroyed(StateId stateId)
    {
        List<byte[]> paths = _removedPrefixes.Where(kvp => kvp.Value.Exists(st => st <= stateId.Id)).Select(kvp => kvp.Key).ToList();

        foreach (byte[] path in paths)
        {
            (byte[] startKey, byte[] endKey) = TrieStoreByPath.GetDeleteKeyFromNibblePrefix(path);

            if (_logger.IsTrace) _logger.Trace($"Requesting removal {startKey.ToHexString()} - {endKey.ToHexString()}");

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

    private StateId? FindState(Hash256 rootHash, long blockNumber = -1)
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

    private bool GetBranchesToProcess(long blockNumber, Hash256 stateRoot, Stack<PathDataCacheInstance> branches, bool filterDetached, out StateId? latestState)
    {
        StateId? localState = FindState(stateRoot);
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

    private StateId? FindStateWithParent(Hash256 rootHash, long? highestBlockNumber = null)
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

    private bool WasRemovedBetween(Span<byte> path, int fromStateId, int toStateId)
    {
        if (path.Length >= _prefixLength && _removedPrefixes.TryGetValue(path[0.._prefixLength], out List<int> deletedAtStates))
        {
            for (int i = 0; i < deletedAtStates.Count; i++)
                if (deletedAtStates[i] > fromStateId && deletedAtStates[i] <= toStateId) return true;
        }
        return false;
    }

    private StateId? SplitAt(StateId stateId)
    {
        //create a new branch
        PathDataCacheInstance newBranchExisting = new(_trieStore, _logger, _lastState, this, _prefixLength, _branches);

        foreach (KeyValuePair<byte[], PathDataHistory> histEntry in _historyByPath)
        {
            PathDataHistory? newHist = histEntry.Value.SplitAt(stateId.Id);
            if (newHist is not null)
                newBranchExisting.Add(histEntry.Key, newHist);
        }
        _branches.Clear();
        _branches.Add(newBranchExisting);

        _lastState = stateId.ParentBlock;
        stateId.ParentBlock = null;

        return stateId;
    }

    public void RemoveDuplicatedInstance(PathDataCacheInstance newInstance)
    {
        for (int i = _branches.Count - 1; i >= 0; i--)
        {
            _branches[i].RemoveDuplicatedInstance(newInstance);

            if (_branches[i] != newInstance &&
                _branches[i]._lastState.BlockStateRoot == newInstance._lastState.BlockStateRoot &&
                _branches[i]._lastState.BlockNumber == newInstance._lastState.BlockNumber)
            {
                _branches.RemoveAt(i);
            }
        }
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
    public PathDataCache(ITrieStore trieStore, ILogManager? logManager, int prefixLength = 66)
    {
        PrefixLength = prefixLength;
        _logger = logManager?.GetClassLogger<PathDataCache>() ?? throw new ArgumentNullException(nameof(logManager));
        _main = new PathDataCacheInstance(trieStore, _logger, PrefixLength);
    }

    private readonly ILogger _logger;

    private readonly ReaderWriterLockSlim _lock = new();

    private PathDataCacheInstance _main;
    private PathDataCacheInstance _openedInstance;

    public int PrefixLength { get; internal set; } = 66;

    public void OpenContext(long blockNumber, Hash256 parentStateRoot)
    {
        try
        {
            _lock.EnterWriteLock();

            if (_openedInstance is not null)
            {
                if (_openedInstance.LatestParentStateRootHash == parentStateRoot)
                    return;

                _openedInstance.RollbackOpenedState();
                if (_openedInstance.IsEmpty)
                    _openedInstance = null;
            }
            if (_openedInstance is not null)
                throw new ArgumentException($"Cannot open instance for {parentStateRoot} - already opened for {_openedInstance.LatestParentStateRootHash}");

            if (_logger.IsTrace) _logger.Trace($"Opening context for {parentStateRoot}");

            _openedInstance = GetCacheInstanceForState(parentStateRoot, blockNumber);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void CloseContext(long blockNumber, Hash256 newStateRoot)
    {
        try
        {
            _lock.EnterWriteLock();

            if (_openedInstance is null)
            {
                if (_main.IsEmpty)
                {
                    _main.GetCacheInstanceForParent(Keccak.EmptyTreeHash, blockNumber, out bool isParallelBranch);
                    _main.CloseState(newStateRoot);
                }
                return;
            }

            _openedInstance.CloseState(newStateRoot);
            _main.RemoveDuplicatedInstance(_openedInstance);
            _openedInstance = null;

            LogCache($"Cache at end of CloseContext block {blockNumber} new state root: {newStateRoot}");
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void AddNodeData(long blockNuber, TrieNode node)
    {
        try
        {
            _lock.EnterWriteLock();

            if (_openedInstance is null)
            {
                if (_main.IsEmpty)
                    _openedInstance = _main.GetCacheInstanceForParent(Keccak.EmptyTreeHash, blockNuber, out bool isParallelBranch);
                else
                    throw new ArgumentException("No cache instance opened");
            }

            _openedInstance.AddNodeData(blockNuber, node);
        }
        finally { _lock.ExitWriteLock(); }
    }

    public NodeData? GetNodeDataAtRoot(Hash256? rootHash, Span<byte> path)
    {
        try
        {
            _lock.EnterReadLock();

            if (rootHash is not null)
            {
                return FindCacheInstanceForStateRoot(rootHash)?.GetNodeDataAtRoot(rootHash, path);
            }
            return _main.GetNodeDataAtRoot(null, path);
        }
        finally { _lock.ExitReadLock(); }
    }

    public NodeData? GetNodeData(Span<byte> path, Hash256? hash)
    {
        try
        {
            _lock.EnterReadLock();

            NodeData? data = null;
            if (_openedInstance is not null)
                data = _openedInstance.GetNodeData(path, hash);

            if (data is null)
            {
                return _main.GetNodeData(path, hash);
            }
            return data;
        }
        finally { _lock.ExitReadLock(); }
    }

    public bool PersistUntilBlock(long blockNumber, Hash256 rootHash, IColumnsWriteBatch<StateColumns>? batch = null)
    {
        try
        {
            _lock.EnterWriteLock();

            _main.PersistUntilBlock(blockNumber, rootHash, batch);

            LogCache($"After persisting block {blockNumber} / {rootHash}");
            return true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void AddRemovedPrefix(long blockNumber, ReadOnlySpan<byte> keyPrefix)
    {
        try
        {
            _lock.EnterWriteLock();

            if (_openedInstance is null)
                throw new ArgumentException("No cache instance opened");

            _openedInstance.AddRemovedPrefix(blockNumber, keyPrefix);
        }
        finally { _lock.ExitWriteLock(); }
    }

    private PathDataCacheInstance? GetCacheInstanceForState(Hash256 parentStateRoot, long blockNumber)
    {
        PathDataCacheInstance newInstance = _main.GetCacheInstanceForParent(parentStateRoot, blockNumber, out bool isParallelBranch);
        //PathDataCacheInstance newInstance = null;
        //foreach (PathDataCacheInstance mainInstance in _mains)
        //{
        //    if ((newInstance = mainInstance.GetCacheInstanceForParent(parentStateRoot, blockNumber)) is not null)
        //        break;
        //}
        //newInstance ??= _mains[0].GetCacheInstanceForPersisted(parentStateRoot);

        return newInstance;
    }

    private PathDataCacheInstance? FindCacheInstanceForStateRoot(Hash256 stateRoot)
    {
        return _main.FindCacheInstanceForStateRoot(stateRoot);
    }

    public void LogCache(string msg)
    {
        if (!_logger.IsTrace)
            return;

        _logger.Trace(msg);

        _main.LogCacheContents(0);
    }
}
