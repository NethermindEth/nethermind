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
        static int _stateIdSeed;
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
            foreach (PathDataAtState nodeHist in _nodes.Reverse())
            {
                if (nodeHist.Data.Keccak == keccak)
                    return nodeHist;
            }
            return null;
        }

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

    class PathDataHistoryColumn
    {
        private readonly SpanDictionary<byte, PathDataHistory> _historyByPath = new(Bytes.SpanNibbleEqualityComparer);
        private readonly SpanDictionary<byte, List<int>> _removedPrefixes;
        private readonly int _prefixLength;

        private StateColumns Column { get; }

        public bool IsEmpty => _historyByPath.Count == 0 && _removedPrefixes.Count == 0;

        public PathDataHistoryColumn(StateColumns column, int prefixLength)
        {
            Column = column;
            _prefixLength = prefixLength;
            _removedPrefixes = new SpanDictionary<byte, List<int>>(Bytes.SpanNibbleEqualityComparer);
        }

        public void AddNodeData(StateId stateId, Span<byte> path, NodeData data, bool shouldPersist)
        {
            GetHistoryForPath(path).Add(stateId.Id, data, shouldPersist);
        }

        public void AddRemovedPrefix(StateId stateId, ReadOnlySpan<byte> keyPrefix)
        {
            if (!_removedPrefixes.TryGetValue(keyPrefix, out List<int> destroyedAtStates))
            {
                destroyedAtStates = new List<int>();
                _removedPrefixes[keyPrefix] = destroyedAtStates;
            }
            destroyedAtStates.Add(stateId.Id);
        }

        public NodeData? GetNodeDataAtRoot(StateId stateId, Span<byte> path)
        {
            PathDataAtState? latestDataHeld = null;
            if (_historyByPath.TryGetValue(path, out PathDataHistory pathHistory))
            {
                latestDataHeld = pathHistory.GetLatestUntil(stateId.Id);
            }

            if (WasRemovedBetween(path, latestDataHeld?.StateId ?? -1, stateId.Id))
            {
                Pruning.Metrics.LoadedFromCacheNodesCount++;
                return new NodeData(null, Keccak.OfAnEmptySequenceRlp);
            }

            if (latestDataHeld is not null)
            {
                Pruning.Metrics.LoadedFromCacheNodesCount++;
                return latestDataHeld.Data;
            }

            return null;
        }

        public NodeData? GetNodeData(Span<byte> path, Hash256? hash)
        {
            return _historyByPath.TryGetValue(path, out PathDataHistory history) ? history.Get(hash)?.Data : null;
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

        public void PersistUntilBlock(StateId? stateId, ITrieStore trieStore, IWriteBatch? batch = null)
        {
            ProcessDestroyed(stateId, trieStore, batch);

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
                }
            }

            foreach (Tuple<byte[], int, byte[]>? data in toPersist)
            {
                trieStore.PersistNodeData(data.Item1, data.Item2, data.Item3, batch);
            }
        }

        private void ProcessDestroyed(StateId stateId, ITrieStore trieStore, IWriteBatch? writeBatch)
        {
            List<byte[]> paths = _removedPrefixes.Where(kvp => kvp.Value.Exists(st => st <= stateId.Id)).Select(kvp => kvp.Key).ToList();

            foreach (byte[] path in paths)
            {
                (byte[] startKey, byte[] endKey) = TrieStoreByPath.GetDeleteKeyFromNibblePrefix(path);

                trieStore.DeleteByRange(startKey, endKey, writeBatch);
            }
        }

        public void PruneUntil(StateId stateId)
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

        public PathDataHistoryColumn SplitAt(StateId stateId)
        {
            PathDataHistoryColumn newHistoryColumn = new(Column, _prefixLength);
            foreach (KeyValuePair<byte[], PathDataHistory> histEntry in _historyByPath)
            {
                PathDataHistory? newHist = histEntry.Value.SplitAt(stateId.Id);
                if (newHist is not null)
                    newHistoryColumn.Add(histEntry.Key, newHist);
            }
            return newHistoryColumn;
        }

        private void Add(byte[] path, PathDataHistory history)
        {
            _historyByPath.Add(path, history);
        }
    }

    public PathDataCacheInstance(ITrieStore trieStore, ILogger? logger, int prefixLength = 66)
    {
        _trieStore = trieStore;
        _logger = logger;
        _branches = new List<PathDataCacheInstance>();
        _prefixLength = prefixLength;
        _historyColumns = new Dictionary<StateColumns, PathDataHistoryColumn>();
        foreach (StateColumns column in Enum.GetValues(typeof(StateColumns)))
            _historyColumns[column] = new PathDataHistoryColumn(column, prefixLength);
    }

    private PathDataCacheInstance(ITrieStore trieStore, ILogger? logger, StateId lastState, PathDataCacheInstance? parent, int prefixLength = 66, IEnumerable<PathDataCacheInstance> branches = null) :
        this(trieStore, logger, prefixLength)
    {
        _lastState = lastState;
        _parentInstance = parent;
        if (branches is not null)
            _branches.AddRange(branches);
    }

    private StateId? _lastState;

    private List<PathDataCacheInstance> _branches;
    private PathDataCacheInstance? _parentInstance;

    private readonly ITrieStore _trieStore;
    private readonly ILogger _logger;
    private bool _isDirty;
    private int _prefixLength;

    public bool IsOpened => _lastState?.BlockStateRoot is null;
    public bool IsEmpty => _historyColumns.All(pair => pair.Value.IsEmpty);
    public Hash256? LatestParentStateRootHash => _lastState?.ParentStateHash;

    private readonly Dictionary<StateColumns, PathDataHistoryColumn> _historyColumns;

    public void CloseState(Hash256 newStateRootHash)
    {
        if (!IsOpened) return;
        _isDirty = false;
        _lastState.BlockStateRoot = newStateRootHash;
    }

    public void RollbackOpenedState()
    {
        if (IsOpened && !_isDirty)
        {
            _lastState = _lastState?.ParentBlock;
        }
    }

    public PathDataCacheInstance? GetCacheInstanceForParent(Hash256 parentStateRoot, long blockNumber)
    {
        foreach (PathDataCacheInstance branch in _branches)
        {
            PathDataCacheInstance innerCache = branch.GetCacheInstanceForParent(parentStateRoot, blockNumber);
            if (innerCache is not null)
                return innerCache;
        }

        //try to find matching state starting from the latest
        PathDataCacheInstance? instance = FindInstanceForParent(parentStateRoot, blockNumber);
        if (instance is not null)
            return instance;

        foreach (PathDataCacheInstance branch in _branches)
        {
            PathDataCacheInstance innerCache = branch.SplitForParent(parentStateRoot, blockNumber);
            if (innerCache is not null)
                return innerCache;
        }

        if (_lastState is not null && parentStateRoot == _lastState.BlockStateRoot && blockNumber == _lastState.BlockNumber + 1 ||
            _lastState is null && _parentInstance is null)
        {
            PathDataCacheInstance newBranch = new(_trieStore, _logger, new StateId(blockNumber, null, parentStateRoot), this, _prefixLength);
            _branches.Add(newBranch);
            return newBranch;
        }

        return SplitForParent(parentStateRoot, blockNumber);
    }

    private PathDataCacheInstance? FindInstanceForParent(Hash256 parentStateRoot, long blockNumber)
    {
        if (_branches.Count == 0)
        {
            //empty instance, 1st item
            if (_lastState is null)
            {
                _lastState = new StateId(blockNumber, null, parentStateRoot);
                return this;
            }
            //add new state and end of chain
            if (_lastState is not null && parentStateRoot == _lastState.BlockStateRoot && blockNumber == _lastState.BlockNumber + 1)
            {
                _lastState = new StateId(blockNumber, null, parentStateRoot, _lastState);
                return this;
            }
        }

        foreach (PathDataCacheInstance branch in _branches)
        {
            PathDataCacheInstance innerCache = branch.FindInstanceForParent(parentStateRoot, blockNumber);
            if (innerCache is not null)
                return innerCache;
        }
        return null;
    }

    private PathDataCacheInstance? SplitForParent(Hash256 parentStateRoot, long blockNumber)
    {
        foreach (PathDataCacheInstance branch in _branches)
        {
            PathDataCacheInstance innerCache = branch.SplitForParent(parentStateRoot, blockNumber);
            if (innerCache is not null)
                return innerCache;
        }

        StateId? splitState = _lastState;
        while (splitState is not null)
        {
            if (splitState.ParentBlock?.BlockStateRoot == parentStateRoot && splitState.BlockNumber <= blockNumber ||
                splitState.ParentStateHash == parentStateRoot && splitState.BlockNumber == blockNumber && _parentInstance is null)
                break;
            splitState = splitState.ParentBlock;
        }

        if (splitState is not null)
        {
            SplitAt(splitState);
            PathDataCacheInstance newBranch = new(_trieStore, _logger, new StateId(blockNumber, null, parentStateRoot), this, _prefixLength);
            _branches.Add(newBranch);
            return newBranch;
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
        _historyColumns[StateColumns.State].AddRemovedPrefix(_lastState, keyPrefix);
        _historyColumns[StateColumns.Storage].AddRemovedPrefix(_lastState, keyPrefix);

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

            StateColumns intermediateColumn = GetColumn(pathToNode.Length);
            _historyColumns[intermediateColumn].AddNodeData(_lastState, pathToNode, nd, false);
        }

        StateColumns column = GetColumn(node.FullPath.Length);
        _historyColumns[column].AddNodeData(_lastState, node.FullPath, nd, true);

        _isDirty = true;
    }

    public NodeData? GetNodeDataAtRoot(Hash256? rootHash, Span<byte> path)
    {
        rootHash ??= _lastState?.BlockStateRoot;
        StateId localState = FindState(rootHash);
        if (localState is not null)
        {
            StateColumns column = GetColumn(path.Length);
            NodeData? nodeData = _historyColumns[column].GetNodeDataAtRoot(localState, path);
            if (nodeData is not null)
                return nodeData;
        }
        return _parentInstance?.GetNodeDataAtRoot(null, path);
    }

    public NodeData? GetNodeData(Span<byte> path, Hash256? hash)
    {
        StateColumns column = GetColumn(path.Length);
        NodeData? data = _historyColumns[column].GetNodeData(path, hash);
        if (data is not null) return data;

        foreach (PathDataCacheInstance branch in _branches)
        {
            data = branch.GetNodeData(path, hash);
            if (data is not null)
                break;
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
        Stack<PathDataCacheInstance> branches = new();
        GetBranchesToProcess(blockNumber, rootHash, branches, false, out StateId? latestState);

        if (branches.Count == 0)
            return false;

        while (branches.Count > 1)
            branches.Pop();

        PathDataCacheInstance branch = branches.Pop();
        branch.PruneUntilInner(latestState);

        if (branch != this)
        {
            for (int i = 0; i < branch._branches.Count; i++)
                branch._branches[i]._parentInstance = this;

            _branches = branch._branches;
            foreach (StateColumns column in Enum.GetValues(typeof(StateColumns)))
                _historyColumns[column] = branch._historyColumns[column];
            _lastState = branch._lastState;
        }

        return true;
    }

    private void PruneUntilInner(StateId stateId)
    {
        foreach (StateColumns column in Enum.GetValues(typeof(StateColumns)))
            _historyColumns[column].PruneUntil(stateId);

        StateId? childOfPersisted = FindStateWithParent(stateId.BlockStateRoot, stateId.BlockNumber + 1);
        if (childOfPersisted is not null)
            childOfPersisted.ParentBlock = null;
        else
            _lastState = null;
    }

    private void PersistUntilBlockInner(StateId? stateId, IColumnsWriteBatch<StateColumns>? batch = null)
    {
        if (_logger.IsTrace)
            _logger.Trace($"Persisting cache instance with latest state {_lastState?.BlockNumber} / {_lastState?.BlockStateRoot} until state {stateId?.BlockNumber} / {stateId?.BlockStateRoot}");

        foreach (StateColumns column in Enum.GetValues(typeof(StateColumns)))
            _historyColumns[column].PersistUntilBlock(stateId, _trieStore, batch?.GetColumnBatch(column));
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

    private void SplitAt(StateId stateId)
    {
        //create a new branch
        PathDataCacheInstance newBranchExisting = new(_trieStore, _logger, _lastState, this, _prefixLength, _branches);

        foreach (StateColumns column in Enum.GetValues(typeof(StateColumns)))
            newBranchExisting._historyColumns[column] = _historyColumns[column].SplitAt(stateId);

        _branches.Add(newBranchExisting);

        _lastState = stateId.ParentBlock;
        stateId.ParentBlock = null;
    }

    public void RemoveDuplicatedInstance(PathDataCacheInstance newInstance)
    {
        if (newInstance._parentInstance is not null)
        {
            List<PathDataCacheInstance> siblings = newInstance._parentInstance._branches;
            for (int i = siblings.Count - 1; i >= 0; i--)
            {
                if (siblings[i] != newInstance)
                {
                    StateId sameState = siblings[i].FindState(newInstance._lastState.BlockStateRoot, newInstance._lastState.BlockNumber ?? -1);
                    if (sameState?.ParentStateHash == newInstance._lastState.ParentStateHash)
                        siblings.Remove(newInstance);
                }
            }
        }
    }

    private StateColumns GetColumn(int pathLength)
    {
        return pathLength >= 66 ? StateColumns.Storage : StateColumns.State;
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
        _readCache = new SpanConcurrentDictionary<byte, byte[]>(Bytes.SpanNibbleEqualityComparer);
    }

    private readonly ILogger _logger;

    private readonly ReaderWriterLockSlim _lock = new();

    private PathDataCacheInstance _main;
    private PathDataCacheInstance? _openedInstance;

    public int PrefixLength { get; internal set; }

    private SpanConcurrentDictionary<byte, byte[]> _readCache;

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

            if (_logger.IsTrace) _logger.Trace($"Opening context for {parentStateRoot} at block {blockNumber}");

            _openedInstance = _main.GetCacheInstanceForParent(parentStateRoot, blockNumber);
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
                    _main.GetCacheInstanceForParent(Keccak.EmptyTreeHash, blockNumber);
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
                    _openedInstance = _main.GetCacheInstanceForParent(Keccak.EmptyTreeHash, blockNuber);
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
                return _main.FindCacheInstanceForStateRoot(rootHash)?.GetNodeDataAtRoot(rootHash, path);

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

            return data ?? _main.GetNodeData(path, hash);
        }
        finally { _lock.ExitReadLock(); }
    }

    public bool PersistUntilBlock(long blockNumber, Hash256 rootHash, IColumnsWriteBatch<StateColumns>? batch = null)
    {
        try
        {
            _lock.EnterWriteLock();

            _main.PersistUntilBlock(blockNumber, rootHash, batch);
            _readCache.Clear();

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

    public void AddDataToReadCache(Span<byte> key, byte[] data)
    {
        _readCache.TryAdd(key, data);
    }

    public byte[]? GetDataFromReadCache(Span<byte> key)
    {
        if (_readCache.TryGetValue(key, out byte[] data))
            return data;
        return null;
    }

    public void LogCache(string msg)
    {
        if (!_logger.IsTrace)
            return;

        _logger.Trace(msg);

        _main.LogCacheContents(0);
    }
}
