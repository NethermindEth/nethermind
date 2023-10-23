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

namespace Nethermind.Trie.ByPath;

public class NodeData
{
    public Keccak Keccak { get; }
    public byte[]? RLP { get; }

    public NodeData(byte[] data, Keccak keccak) { RLP = data; Keccak = keccak; }
}


public class PathDataCache : IPathDataCache
{
    class StateId
    {
        static int _stateIdSeed = 0;
        public int Id { get; }
        public long BlockNumber { get; set; }
        public Keccak BlockHash { get; set; }

        public StateId? ParentBlock { get; set; }

        public StateId(long blockNumber, Keccak blockHash, StateId? parentBlock)
        {
            Id = Interlocked.Increment(ref _stateIdSeed);
            BlockNumber = blockNumber;
            BlockHash = blockHash;
            ParentBlock = parentBlock;
        }

        private StateId(int id, long blockNumber, Keccak blockHash, StateId? parentBlock)
        {
            Id = id;
            BlockNumber = blockNumber;
            BlockHash = blockHash;
            ParentBlock = parentBlock;
        }

        public StateId Clone()
        {
            return new StateId(Id, BlockNumber, BlockHash, ParentBlock);
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

                if (_nodes.Count == 0 || stateId < _nodes.Min.StateId || stateId > _nodes.Max.StateId)
                    return null;

                SortedSet<PathDataAtState> viewUntilBlock = _nodes.GetViewBetween(new PathDataAtState(stateId), _nodes.Max);
                PathDataAtState[] copy = new PathDataAtState[viewUntilBlock.Count];
                viewUntilBlock.CopyTo(copy);
                PathDataHistory newHistory = new PathDataHistory(copy);
                viewUntilBlock.Clear();
                return newHistory;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }
    }

    public PathDataCache(ITrieStore trieStore, ILogManager? logManager)
    {
        _trieStore = trieStore;
        _logger = logManager?.GetClassLogger<TrieNodePathCache>() ?? throw new ArgumentNullException(nameof(logManager));
        _branches = new ConcurrentBag<PathDataCache>();
        _isDetached = false;
        _transientStore = new SpanConcurrentDictionary<byte, NodeData>(Bytes.SpanNibbleEqualityComparer);
    }

    private PathDataCache(ITrieStore trieStore, ILogger? logger, StateId lastState, IEnumerable<PathDataCache> branches = null, bool isDetached = false)
    {
        _lastState = lastState;
        _trieStore = trieStore;
        _logger = logger;
        _branches = new ConcurrentBag<PathDataCache>();
        _isDetached = isDetached;
        _transientStore = new SpanConcurrentDictionary<byte, NodeData>(Bytes.SpanNibbleEqualityComparer);
        if (branches is not null)
        {
            foreach (var b in branches)
                _branches.Add(b);
        }
    }

    private StateId _lastState;
    private bool _isDetached;

    private SpanConcurrentDictionary<byte, PathDataHistory> _historyByPath = new(Bytes.SpanNibbleEqualityComparer);
    private ConcurrentBag<PathDataCache> _branches;
    private Keccak _context;
    private SpanConcurrentDictionary<byte, NodeData> _transientStore;

    private readonly ITrieStore _trieStore;
    private readonly ILogger _logger;

    public void SetContext(Keccak keccak)
    {
        if (_lastState is not null)
            SetContextInner(keccak);
    }

    public bool EnsureStateHistoryExists(long blockNuber, Keccak stateHash)
    {
        if (_lastState is null)
        {
            _lastState = new StateId(blockNuber, stateHash, null);
            if (_logger.IsTrace) _logger.Trace($"New initial state {blockNuber} / {stateHash}");
        }

        StateId stateId = FindState(stateHash, blockNuber);
        if (stateId is null)
        {
            foreach (PathDataCache branch in _branches)
            {
                bool handledInBranch = branch.EnsureStateHistoryExists(blockNuber, stateHash);
                if (handledInBranch)
                {
                    PrintStates("State tree after branch prep", 0);
                    return true;
                }
            }
            PathDataCache prepBranch = PrepareBranch(blockNuber, stateHash);
            PrintStates("State tree after branch prep", 0);
            return prepBranch is not null ? prepBranch.EnsureStateHistoryExists(blockNuber, stateHash) : false;
        }
        return true;
    }

    private PathDataCache GetCacheInstance(long blockNuber, Keccak stateHash)
    {
        if (_lastState is null)
        {
            _lastState = new StateId(blockNuber, stateHash, null);
            return this;
        }

        StateId stateId = FindState(stateHash, blockNuber);
        if (stateId is null)
        {
            foreach (PathDataCache branch in _branches)
            {
                PathDataCache innerCache = branch.GetCacheInstance(blockNuber, stateHash);
                if (innerCache is not null)
                    return innerCache;
            }
            return PrepareBranch(blockNuber, stateHash);
        }
        return this;
    }

    public void AddNodeDataTransient(TrieNode node)
    {
        NodeData nd = new(node.FullRlp.Array, node.Keccak);
        if (node.IsLeaf)
            _transientStore[node.StoreNibblePathPrefix.Concat(node.PathToNode).ToArray()] = nd;
        _transientStore[node.FullPath] = nd;
    }

    public void MoveTransientData(long blockNumber, Keccak stateRoot)
    {
        PathDataCache targetInstabce = GetCacheInstance(blockNumber, stateRoot);
        if (targetInstabce is null)
            throw new Exception("Unable to create target cache instance!");

        PrintStates("MoveTransientData - block state tree", 0);

        StateId targetState = targetInstabce.FindState(stateRoot, blockNumber);
        foreach (KeyValuePair<byte[], NodeData> tmpVal in _transientStore)
        {
            targetInstabce.AddNodeData(targetState, tmpVal.Key, tmpVal.Value);
        }
    }

    public void AddNodeData(long blockNuber, Keccak stateHash, TrieNode node)
    {
        if (_logger.IsTrace) _logger.Trace($"Adding node {node.PathToNode.ToHexString()} / {node.FullPath.ToHexString()} with Keccak: {node.Keccak} at block {blockNuber}");
        if (_lastState is null)
        {
            _lastState = new StateId(blockNuber, stateHash, null);
            if (_logger.IsTrace) _logger.Trace($"New state {blockNuber} / {stateHash}");
        }

        AddNodeDataInner(blockNuber, stateHash, node);
    }

    private void AddNodeData(StateId stateId, Span<byte> path, NodeData nodeData)
    {
        PathDataHistory hist = GetHistoryForPath(path);
        hist.Add(stateId.Id, nodeData, true);
    }

    private bool AddNodeDataInner(long blockNuber, Keccak stateHash, TrieNode node)
    {
        StateId stateId = FindState(stateHash, blockNuber);
        if (stateId is null)
        {
            foreach (PathDataCache branch in _branches)
            {
                bool handledInBranch = branch.AddNodeDataInner(blockNuber, stateHash, node);
                if (handledInBranch)
                    return true;
            }

            PathDataCache prepBranch = PrepareBranch(blockNuber, stateHash);
            return prepBranch is not null ? prepBranch.AddNodeDataInner(blockNuber, stateHash, node) : false;
        }
        else
        {
            NodeData nd = new(node.FullRlp.Array, node.Keccak);
            if (node.IsLeaf)
            {
                PathDataHistory leafPointerHist = GetHistoryForPath(node.PathToNode);
                leafPointerHist.Add(stateId.Id, nd, false);
            }
            PathDataHistory history = GetHistoryForPath(node.FullPath);
            history.Add(stateId.Id, nd, true);

            return true;
        }
    }

    public NodeData? GetNodeDataAtRoot(Keccak? rootHash, Span<byte> path)
    {
        if (rootHash is null)
        {
            if (_historyByPath.TryGetValue(path, out PathDataHistory history))
            {
                PathDataAtState dataAtState = history.GetLatest();
                if (dataAtState is not null)
                    Pruning.Metrics.LoadedFromCacheNodesCount++;
                return dataAtState.Data;
            }
        }
        else
        {
            bool rootInBranch = false;
            NodeData foundData = null;
            foreach (PathDataCache branchCache in _branches)
            {
                rootInBranch = branchCache.GetNodeDataAtRoot(rootHash, path, out foundData);
                if (rootInBranch)
                    break;
            }

            StateId localState = FindState(rootHash);
            if (rootInBranch && foundData is null)
                localState = _lastState;

            if (localState is not null && _historyByPath.TryGetValue(path, out PathDataHistory pathHistory))
            {
                PathDataAtState? data = pathHistory.GetLatestUntil(localState.Id);
                if (data is not null)
                {
                    Pruning.Metrics.LoadedFromCacheNodesCount++;
                    return data.Data;
                }
            }
        }
        return null;
    }

    public NodeData? GetNodeData(Span<byte> path, Keccak? hash)
    {
        foreach (var cache in _branches)
        {
            NodeData? data = cache.GetNodeData(path, hash);
            if (data is not null)
                return data;
        }
        if (_historyByPath.TryGetValue(path, out PathDataHistory history))
        {
            return history.Get(hash)?.Data;
        }
        return null;
    }

    private bool GetNodeDataAtRoot(Keccak? rootHash, Span<byte> path, out NodeData? nodeData)
    {
        nodeData = null;
        StateId stateId = FindState(rootHash);
        if (stateId is not null)
        {
            if (_historyByPath.TryGetValue(path, out PathDataHistory pathHistory))
            {
                PathDataAtState? data = pathHistory.GetLatestUntil(stateId.Id);
                if (data is not null)
                {
                    nodeData = data.Data;
                    Pruning.Metrics.LoadedFromCacheNodesCount++;
                }
            }
            return true;
        }
        return false;
    }

    public bool PersistUntilBlock(long blockNumber, Keccak rootHash, IBatch? batch = null)
    {
        Stack<PathDataCache> branches = new Stack<PathDataCache>();
        GetBranchesToProcess(blockNumber, rootHash, branches, true, out StateId? latestState);
        while (branches.Count > 0)
        {
            PathDataCache branch = branches.Pop();
            branch.PersistUntilBlockInner(latestState, batch);
        }

        PruneUntil(blockNumber, rootHash);

        return true;
    }

    private void PersistUntilBlockInner(StateId? stateId, IBatch? batch = null)
    {
        List<TrieNode> toPersist = new();
        foreach (KeyValuePair<byte[], PathDataHistory> nodeVersion in _historyByPath)
        {
            PathDataAtState? nodeData = nodeVersion.Value.GetLatestUntil(stateId.Id);
            if (nodeData?.ShouldPersist == true)
            {
                TrieNode node;
                if (nodeVersion.Key.Length >= 66)
                {
                    byte[] prefix = nodeVersion.Key.Slice(0, 66);
                    node = new(NodeType.Unknown, nodeVersion.Key.Slice(66), nodeData.Data.Keccak, nodeData.Data.RLP);
                    node.StoreNibblePathPrefix = prefix;
                }
                else
                {
                    node = new(NodeType.Unknown, nodeVersion.Key, nodeData.Data.Keccak, nodeData.Data.RLP);
                }

                if (nodeData.Data.RLP is not null)
                {
                    node.ResolveNode(_trieStore);
                    node.ResolveKey(_trieStore, nodeVersion.Key.Length == 0);
                }
                if (nodeData.Data.RLP is null) toPersist.Insert(0, node); else toPersist.Add(node);
            }
        }

        foreach (TrieNode node in toPersist)
        {
            _trieStore.SaveNodeDirectly(stateId.BlockNumber, node, batch);
            if (_logger.IsTrace) _logger.Trace($"Persising node {node.PathToNode.ToHexString()} / {node.FullPath.ToHexString()} with Keccak: {node.Keccak} at block {stateId.BlockNumber} / {stateId.BlockHash} Value: {node.FullRlp.ToArray()?.ToHexString()}");
        }
    }

    public bool PruneUntil(long blockNumber, Keccak rootHash)
    {
        Stack<PathDataCache> branches = new Stack<PathDataCache>();
        GetBranchesToProcess(blockNumber, rootHash, branches, false, out StateId? latestState);

        if (branches.Count == 0)
            return false;

        while (branches.Count > 1)
            branches.Pop();

        PathDataCache branch = branches.Pop();
        branch.PruneUntilInner(latestState);

        if (branch != this)
        {
            _branches = branch._branches;
            _historyByPath = branch._historyByPath;
            _lastState = branch._lastState;
        }

        PrintStates("State after persist", 0);

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

        StateId? childOfPersisted = FindStateAfter(stateId.BlockHash, stateId.BlockNumber);
        if (childOfPersisted is not null)
            childOfPersisted.ParentBlock = null;
        else
            _lastState = null;
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
            if (stateId.BlockHash == rootHash && (stateId.BlockNumber == blockNumber || blockNumber == -1))
                return stateId;
            stateId = stateId.ParentBlock;
        }
        return null;
    }

    private bool SetContextInner(Keccak context)
    {
        foreach (PathDataCache branchCache in _branches)
        {
            branchCache.SetContextInner(context);
        }

        StateId? localState = FindState(context);
        if (localState is not null)
        {
            if (_logger.IsTrace) _logger.Trace($"Setting context to {context} at state {localState.BlockNumber} / {localState.BlockHash}");
            _context = context;
            return true;
        }
        return false;
    }

    private bool GetBranchesToProcess(long blockNumber, Keccak stateRoot, Stack<PathDataCache> branches, bool filterDetached, out StateId? latestState)
    {
        StateId? localState = FindState(stateRoot, blockNumber);
        if (localState is not null)
        {
            branches.Push(this);
            latestState = localState;
            return true;
        }

        foreach (PathDataCache branchCache in _branches)
        {
            if (branchCache.GetBranchesToProcess(blockNumber, stateRoot, branches, filterDetached, out latestState))
            {
                if (!filterDetached || !branchCache._isDetached)
                    branches.Push(this);
                return true;
            }
        }
        latestState = null;
        return false;
    }

    private StateId? FindStateIncludingBranches(Keccak rootHash, long blockNumber = -1)
    {
        foreach (PathDataCache branchCache in _branches)
        {
            StateId? stateInBranch = branchCache.FindStateIncludingBranches(rootHash, blockNumber);
            if (stateInBranch is not null)
                return stateInBranch;
        }

        StateId stateId = _lastState;
        while (stateId is not null)
        {
            if (stateId.BlockHash == rootHash && (stateId.BlockNumber == blockNumber || blockNumber == -1))
                return stateId;
            stateId = stateId.ParentBlock;
        }
        return null;
    }

    private StateId? FindStateAfter(Keccak rootHash, long blockNumber)
    {
        StateId stateId = _lastState;
        while (stateId.ParentBlock is not null)
        {
            if (stateId.ParentBlock.BlockHash == rootHash && stateId.ParentBlock.BlockNumber == blockNumber)
                return stateId;
            stateId = stateId.ParentBlock;
        }
        return null;
    }

    private StateId? FindStateWithBlockNumberOrEarlier(long blockNumber)
    {
        StateId stateId = _lastState;
        while (stateId is not null)
        {
            if (stateId.BlockNumber <= blockNumber)
                return stateId;
            stateId = stateId.ParentBlock;
        }
        return null;
    }

    private void Add(Span<byte> path, PathDataHistory history)
    {
        _historyByPath[path] = history;
    }

    private PathDataCache? PrepareBranch(long blockNumber, Keccak stateHash)
    {
        PathDataCache newBranch = null;

        //can add to end of chain?
        if (_lastState.BlockHash == _context && _lastState.BlockNumber < blockNumber)
        {
            if (_logger.IsTrace) _logger.Trace($"Adding new state in current chain {blockNumber} / {stateHash} parent: {_lastState.BlockNumber} / {_lastState.BlockHash}");
            _lastState = new StateId(blockNumber, stateHash, _lastState);
            newBranch = this;
        }
        else
        {
            //create a new branch
            StateId? currState = FindStateWithBlockNumberOrEarlier(blockNumber);
            if (currState is not null)
            {
                if (currState.BlockNumber == blockNumber)
                {
                    if (_context is not null && currState.ParentBlock?.BlockHash == _context)
                    {
                        PathDataCache newBranchExisting = new(_trieStore, _logger, _lastState, _branches);

                        foreach (KeyValuePair<byte[], PathDataHistory> histEntry in _historyByPath)
                        {
                            PathDataHistory? newHist = histEntry.Value.SplitAt(currState.Id);
                            if (newHist is not null)
                                newBranchExisting.Add(histEntry.Key, newHist);
                        }
                        _branches.Clear();
                        _branches.Add(newBranchExisting);

                        newBranch = new(_trieStore, _logger, new StateId(blockNumber, stateHash, null), isDetached: currState.ParentBlock is null);
                        _branches.Add(newBranch);

                        _lastState = currState.ParentBlock;
                        currState.ParentBlock = null;
                    }
                    else if (currState.ParentBlock is null)
                    {
                        newBranch = new(_trieStore, _logger, new StateId(blockNumber, stateHash, null), isDetached: currState.ParentBlock is null);
                        _branches.Add(newBranch);
                    }
                }
                else if (_context is not null && currState.BlockNumber < blockNumber && currState.BlockHash == _context)
                {
                    newBranch = new(_trieStore, _logger, new StateId(blockNumber, stateHash, null), isDetached: currState.ParentBlock is null);
                    _branches.Add(newBranch);
                }
            }
        }

        return newBranch;
    }

    private void PrintStates(string topLevelMsg, int level)
    {
        if (!_logger.IsTrace)
            return;

        _logger.Trace(topLevelMsg);

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
            sb.Append($"[B {st.BlockNumber} | H {st.BlockHash} | I {st.Id}] -> ");
        }
        
        _logger.Trace(sb.ToString());

        level++;
        foreach (PathDataCache branch in _branches)
            branch.PrintStates(topLevelMsg, level);
    }
}
