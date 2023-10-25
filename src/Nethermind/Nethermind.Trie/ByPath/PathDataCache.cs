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
        _isDetached = false;
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
    private bool _isDetached;

    private SpanConcurrentDictionary<byte, PathDataHistory> _historyByPath = new(Bytes.SpanNibbleEqualityComparer);
    private ConcurrentBag<PathDataCacheInstance> _branches;
    //private Keccak _context;
    private SpanConcurrentDictionary<byte, List<int>> _removedPrefixes;
    private PathDataCacheInstance? _parentInstance;
    private StateId _parentStateId;

    private readonly ITrieStore _trieStore;
    private readonly ILogger _logger;
    private bool _isDirty;
    public bool IsDirty => _isDirty;

    public int PrefixLength { get; set; } = 66;

    public bool IsOpened => _lastState.BlockStateRoot is null;
    public bool IsEmpty => _historyByPath.Count == 0 && _removedPrefixes.Count == 0;

    //public bool EnsureStateHistoryExists(long blockNuber, Keccak stateHash)
    //{
    //    if (_lastState is null)
    //    {
    //        _lastState = new StateId(blockNuber, stateHash, null);
    //        if (_logger.IsTrace) _logger.Trace($"New initial state {blockNuber} / {stateHash}");
    //    }

    //    StateId stateId = FindState(stateHash, blockNuber);
    //    if (stateId is null)
    //    {
    //        foreach (PathDataCacheInstance branch in _branches)
    //        {
    //            bool handledInBranch = branch.EnsureStateHistoryExists(blockNuber, stateHash);
    //            if (handledInBranch)
    //            {
    //                PrintStates("State tree after branch prep", 0);
    //                return true;
    //            }
    //        }
    //        PathDataCache prepBranch = PrepareBranch(blockNuber, stateHash);
    //        PrintStates("State tree after branch prep", 0);
    //        return prepBranch is not null ? prepBranch.EnsureStateHistoryExists(blockNuber, stateHash) : false;
    //    }
    //    return true;
    //}

    //private PathDataCacheInstance? GetCacheInstance(long blockNuber, Keccak stateHash)
    //{
    //    if (_lastState is null)
    //    {
    //        _lastState = new StateId(blockNuber, stateHash, null);
    //        return this;
    //    }

    //    StateId stateId = FindState(stateHash, blockNuber);
    //    if (stateId is null)
    //    {
    //        foreach (PathDataCacheInstance branch in _branches)
    //        {
    //            PathDataCacheInstance innerCache = branch.GetCacheInstance(blockNuber, stateHash);
    //            if (innerCache is not null)
    //                return innerCache;
    //        }
    //        return PrepareBranchByParent(blockNuber, stateHash);
    //    }
    //    return this;
    //}

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
                if (blockNumber < _lastState.BlockNumber && _parentInstance is null)
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

                    PathDataCacheInstance parentCacheInstance = null;
                    if (st?.ParentBlock is not null)
                    {
                        parentCacheInstance = this;
                    }

                    StateId clonedState = _lastState.Clone();
                    clonedState.BlockStateRoot = stateRootHash;
                    clonedState.BlockNumber = blockNumber;
                    clonedState.ParentBlock = null;
                    clonedState.ParentStateHash = _lastState.ParentStateHash;

                    PathDataCacheInstance newBranch = new(_trieStore, _logger, clonedState, parentCacheInstance, st?.ParentBlock);

                    foreach (KeyValuePair<byte[], PathDataHistory> histEntry in _historyByPath)
                    {
                        PathDataHistory? newHist = histEntry.Value.SplitAt(clonedState.Id);
                        if (newHist is not null)
                            newBranch.Add(histEntry.Key, newHist);
                    }

                    _lastState = _lastState.ParentBlock;

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

    public void RollbackState()
    {
        if (IsOpened)
        {
            _lastState = _lastState.ParentBlock;
        }
    }

        //public PathDataCacheInstance? GetCacheInstanceForParent(Keccak parentStateRoot)
        //{
        //    if (_lastState is null)
        //    {
        //        _lastState = new StateId(null, null, parentStateRoot);
        //        return this;
        //    }

        //    StateId stateId = FindState(parentStateRoot);
        //    if (stateId is null)
        //    {
        //        foreach (PathDataCacheInstance branch in _branches)
        //        {
        //            PathDataCacheInstance innerCache = branch.GetCacheInstanceForParent(parentStateRoot);
        //            if (innerCache is not null)
        //                return innerCache;
        //        }
        //    }
        //    else
        //    {
        //        PathDataCacheInstance? newBranch = null;
        //        if (_lastState.BlockStateRoot == parentStateRoot)
        //        {
        //            newBranch = new PathDataCacheInstance(_trieStore, _logger, new StateId(_lastState.BlockNumber + 1, null, parentStateRoot), this);
        //        }
        //        else
        //        {
        //            //create a new branch
        //            StateId? currState = FindStateAfter(parentStateRoot);
        //            if (currState is not null)
        //            {
        //                PathDataCacheInstance newBranchExisting = new(_trieStore, _logger, _lastState, this, _branches);

        //                foreach (KeyValuePair<byte[], PathDataHistory> histEntry in _historyByPath)
        //                {
        //                    PathDataHistory? newHist = histEntry.Value.SplitAt(currState.Id);
        //                    if (newHist is not null)
        //                        newBranchExisting.Add(histEntry.Key, newHist);
        //                }
        //                _branches.Clear();
        //                _branches.Add(newBranchExisting);

        //                newBranch = new(_trieStore, _logger, new StateId(currState.BlockNumber, null, parentStateRoot), this);
        //                _branches.Add(newBranch);

        //                _lastState = currState.ParentBlock;
        //                currState.ParentBlock = null;
        //            }
        //        }
        //        return newBranch;
        //    }
        //    return null;
        //}

        public PathDataCacheInstance? GetCacheInstanceForParent(Keccak parentStateRoot)
    {
        if (_lastState is null)
        {
            _lastState = new StateId(null, null, parentStateRoot);
            return this;
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

        foreach (PathDataCacheInstance branch in _branches)
        {
            PathDataCacheInstance innerCache = branch.GetCacheInstanceForParent(parentStateRoot);
            if (innerCache is not null)
                return innerCache;
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
        //if (_readyForWrite)
        //    return;

        ////if (!IsEmpty)
        ////    throw new ArgumentException("Can't branch");

        //if (_parentInstance is null || _lastState.ParentStateHash == _parentInstance._lastState.BlockStateRoot)
        //{
        //    //successor of parent - nothing to do
        //}
        //else
        //{
        //    //SplitAt(_lastState.ParentStateHash);
        //    //_parentInstance._branches.Add(this);
        //}
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

    //private void SplitAt(StateId stateId)
    //{
    //    //create a new branch
    //    if (stateId.ParentBlock is not null)
    //    {
    //        PathDataCacheInstance newBranchExisting = new(_trieStore, _logger, _lastState, this, stateId.ParentBlock, _branches);

    //        foreach (KeyValuePair<byte[], PathDataHistory> histEntry in _historyByPath)
    //        {
    //            PathDataHistory? newHist = histEntry.Value.SplitAt(stateId.Id);
    //            if (newHist is not null)
    //                newBranchExisting.Add(histEntry.Key, newHist);
    //        }
    //        _branches.Clear();
    //        _branches.Add(newBranchExisting);

    //        _lastState = stateId.ParentBlock;
    //        stateId.ParentBlock = null;
    //    }
    //}

    public bool MergeToParent()
    {
        if (_parentInstance == null)
            return false;

        if (_parentInstance._lastState.BlockStateRoot == _lastState.ParentStateHash)
        {
            _lastState.ParentBlock = _parentInstance._lastState;
            _parentInstance._lastState = _lastState;

            foreach (KeyValuePair<byte[], PathDataHistory> nodeVersion in _historyByPath)
            {
                if (_parentInstance._historyByPath.TryGetValue(nodeVersion.Key, out PathDataHistory pathDataHistory))
                {
                    pathDataHistory.Merge(nodeVersion.Value);
                }
                else
                {
                    _parentInstance._historyByPath[nodeVersion.Key] = nodeVersion.Value;
                }
            }

            foreach (var kvp in _removedPrefixes)
            {
                if (_parentInstance._removedPrefixes.TryGetValue(kvp.Key, out List<int> stateIds))
                {
                    stateIds.AddRange(kvp.Value);
                }
                else
                {
                    _parentInstance._removedPrefixes[kvp.Key] = kvp.Value;
                }
            }
            return true;
        }
        else
        {
            if (_parentInstance.SplitAt(_lastState.ParentStateHash, _lastState.BlockNumber) is not null)
            {
                _parentInstance._branches.Add(this);
                return true;
            }
            return false;
        }
    }

    public bool CloseAndMergeToParent(long blockNumber, Keccak stateRootHash)
    {
        if (IsOpened)
        {
            if (_lastState.ParentBlock?.BlockNumber == blockNumber && _lastState.ParentBlock?.BlockStateRoot == stateRootHash)
            {
                _lastState = _lastState.ParentBlock;
            }
            else
            {
                if (blockNumber < _lastState.BlockNumber)
                {
                    Keccak searchedParentHash = _lastState.ParentStateHash;
                    StateId st = _lastState;
                    while (st is not null && st.ParentStateHash == searchedParentHash && st.BlockNumber > blockNumber)
                    {
                        st = st.ParentBlock;
                    }
                }

                _lastState.BlockStateRoot = stateRootHash;
                _lastState.BlockNumber = blockNumber;
            }
        }

        if (_parentInstance == null)
            return false;

        if (_parentInstance._lastState.BlockStateRoot == _lastState.ParentStateHash)
        {
            _lastState.ParentBlock = _parentInstance._lastState;
            _parentInstance._lastState = _lastState;

            foreach (KeyValuePair<byte[], PathDataHistory> nodeVersion in _historyByPath)
            {
                if (_parentInstance._historyByPath.TryGetValue(nodeVersion.Key, out PathDataHistory pathDataHistory))
                {
                    pathDataHistory.Merge(nodeVersion.Value);
                }
                else
                {
                    _parentInstance._historyByPath[nodeVersion.Key] = nodeVersion.Value;
                }
            }

            foreach (var kvp in _removedPrefixes)
            {
                if (_parentInstance._removedPrefixes.TryGetValue(kvp.Key, out List<int> stateIds))
                {
                    stateIds.AddRange(kvp.Value);
                }
                else
                {
                    _parentInstance._removedPrefixes[kvp.Key] = kvp.Value;
                }
            }
            return true;
        }
        else
        {
            _parentInstance._branches.Add(this);
            return true;
        }
    }

    //public void AddNodeDataTransient(TrieNode node)
    //{
    //    NodeData nd = new(node.FullRlp.Array, node.Keccak);
    //    if (node.IsLeaf)
    //        _transientStore[node.StoreNibblePathPrefix.Concat(node.PathToNode).ToArray()] = nd;
    //    _transientStore[node.FullPath] = nd;
    //}

    //public void MoveTransientData(long blockNumber, Keccak stateRoot)
    //{
    //    PathDataCache targetInstabce = GetCacheInstance(blockNumber, stateRoot);
    //    if (targetInstabce is null)
    //        throw new Exception("Unable to create target cache instance!");

    //    PrintStates("MoveTransientData - block state tree", 0);

    //    StateId targetState = targetInstabce.FindState(stateRoot, blockNumber);
    //    foreach (KeyValuePair<byte[], NodeData> tmpVal in _transientStore)
    //    {
    //        targetInstabce.AddNodeData(targetState, tmpVal.Key, tmpVal.Value);
    //    }
    //    _transientStore.Clear();
    //}

    public void AddNodeData(long blockNuber, TrieNode node)
    {
        if (!IsOpened)
            throw new ArgumentException("Can't add node to closed cache instance");

        if (Bytes.Comparer.Compare(node.FullPath, Bytes.FromHexString("0808020c0f08000e0208070b080a04040a060c020b050e01090903010f02060d070e0307050e0902020e060f0f04010904030d040907050b03040e0c0902070908000a050f06060c0108070d04030a04010f0e0d040f0a040101060008090f0e030c040b030b000504090305080c050608050702080c0406010400030303020c0005")) == 0)
        {
            int a = 10;
            a++;
        }

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
            else
            {
                return _parentInstance?.GetNodeDataAtRoot(_parentStateId?.BlockStateRoot, path);
            }
        }
        return null;
    }

    public NodeData? GetNodeData(Span<byte> path, Keccak? hash)
    {
        //foreach (var cache in _branches)
        //{
        //    NodeData? data = cache.GetNodeData(path, hash);
        //    if (data is not null)
        //        return data;
        //}
        if (_historyByPath.TryGetValue(path, out PathDataHistory history))
        {
            return history.Get(hash)?.Data;
        }
        return _parentInstance?.GetNodeData(path, hash);
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

        StateId? childOfPersisted = FindStateWithParent(stateId.BlockStateRoot, stateId.BlockNumber);
        if (childOfPersisted is not null)
            childOfPersisted.ParentBlock = null;
        else
            _lastState = null;

        _branches.Clear();
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

    //private PathDataCacheInstance? PrepareBranch(long blockNumber, Keccak stateHash)
    //{
    //    PathDataCacheInstance newBranch = null;

    //    //can add to end of chain?
    //    if (_lastState.BlockHash == _context && _lastState.BlockNumber < blockNumber)
    //    {
    //        if (_logger.IsTrace) _logger.Trace($"Adding new state in current chain {blockNumber} / {stateHash} parent: {_lastState.BlockNumber} / {_lastState.BlockHash}");
    //        _lastState = new StateId(blockNumber, stateHash, _lastState);
    //        newBranch = this;
    //    }
    //    else
    //    {
    //        //create a new branch
    //        StateId? currState = FindStateWithBlockNumberOrEarlier(blockNumber);
    //        if (currState is not null)
    //        {
    //            if (currState.BlockNumber == blockNumber)
    //            {
    //                if (_context is not null && currState.ParentBlock?.BlockHash == _context)
    //                {
    //                    PathDataCacheInstance newBranchExisting = new(_trieStore, _logger, _lastState, _branches);

    //                    foreach (KeyValuePair<byte[], PathDataHistory> histEntry in _historyByPath)
    //                    {
    //                        PathDataHistory? newHist = histEntry.Value.SplitAt(currState.Id);
    //                        if (newHist is not null)
    //                            newBranchExisting.Add(histEntry.Key, newHist);
    //                    }
    //                    _branches.Clear();
    //                    _branches.Add(newBranchExisting);

    //                    newBranch = new(_trieStore, _logger, new StateId(blockNumber, stateHash, null), isDetached: currState.ParentBlock is null);
    //                    _branches.Add(newBranch);

    //                    _lastState = currState.ParentBlock;
    //                    currState.ParentBlock = null;
    //                }
    //                else if (currState.ParentBlock is null)
    //                {
    //                    newBranch = new(_trieStore, _logger, new StateId(blockNumber, stateHash, null), isDetached: currState.ParentBlock is null);
    //                    _branches.Add(newBranch);
    //                }
    //            }
    //            else if (_context is not null && currState.BlockNumber < blockNumber && currState.BlockHash == _context)
    //            {
    //                newBranch = new(_trieStore, _logger, new StateId(blockNumber, stateHash, null), isDetached: currState.ParentBlock is null);
    //                _branches.Add(newBranch);
    //            }
    //        }
    //    }

    //    return newBranch;
    //}

    //private PathDataCacheInstance? PrepareBranchForParent(Keccak parentStateRoot)
    //{
    //    PathDataCacheInstance newBranch = null;

    //    //can add to end of chain?
    //    if (_lastState.BlockStateRoot == parentStateRoot)
    //    {
    //        if (_logger.IsTrace) _logger.Trace($"Adding new state in current chain - parent: {_lastState.BlockNumber} / {_lastState.BlockStateRoot}");
    //        _lastState = new StateId(_lastState.BlockNumber + 1, null, _lastState);
    //        newBranch = this;
    //    }
    //    else
    //    {
    //        //create a new branch
    //        StateId? currState = FindStateAfter(blockNumber);
    //        if (currState is not null)
    //        {
    //            if (currState.BlockNumber == blockNumber)
    //            {
    //                if (currState.ParentBlock?.BlockStateRoot == parentStateRoot)
    //                {
    //                    PathDataCacheInstance newBranchExisting = new(_trieStore, _logger, _lastState, this, _branches);

    //                    foreach (KeyValuePair<byte[], PathDataHistory> histEntry in _historyByPath)
    //                    {
    //                        PathDataHistory? newHist = histEntry.Value.SplitAt(currState.Id);
    //                        if (newHist is not null)
    //                            newBranchExisting.Add(histEntry.Key, newHist);
    //                    }
    //                    _branches.Clear();
    //                    _branches.Add(newBranchExisting);

    //                    newBranch = new(_trieStore, _logger, new StateId(blockNumber, null, null), this);
    //                    _branches.Add(newBranch);

    //                    _lastState = currState.ParentBlock;
    //                    currState.ParentBlock = null;
    //                }
    //                else if (currState.ParentBlock is null)
    //                {
    //                    newBranch = new(_trieStore, _logger, new StateId(blockNumber, null, null), this);
    //                    _branches.Add(newBranch);
    //                }
    //            }
    //            else if (currState.BlockNumber < blockNumber && currState.BlockStateRoot == parentStateRoot)
    //            {
    //                newBranch = new(_trieStore, _logger, new StateId(blockNumber, null, null), this);
    //                _branches.Add(newBranch);
    //            }
    //        }
    //    }

    //    return newBranch;
    //}

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
            if (newInstance._lastState?.ParentBlock == null
                    && newInstance._lastState.BlockNumber == st.BlockNumber
                    && newInstance._lastState.BlockStateRoot == st.BlockStateRoot)
                return true;

            st = st.ParentBlock;
        }
        return false;
    }

    public void PrintStates(string topLevelMsg, int level)
    {
        if (!_logger.IsTrace)
            return;

        if (level == 0)
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
            sb.Append($"[B {st.BlockNumber} | H {st.BlockStateRoot} | I {st.Id}] -> ");
        }

        _logger.Trace(sb.ToString());

        level++;
        foreach (PathDataCacheInstance branch in _branches)
            branch.PrintStates(topLevelMsg, level);
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
            if (!_openedInstance.IsDirty)
            {
                _openedInstance.RollbackState();
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
        //if (_openedInstance is null)
        //    throw new ArgumentException("Nothing to close instance opened");
        //if (_logger is null)
        //    return;

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

        if (_openedInstance == GetMainEqualTo(_openedInstance))
        {
            PathDataCacheInstance? newInstance = _openedInstance.CloseState(blockNumber, newStatRoot);
            if (newInstance is not null && !StateAlreadyInMain(newInstance))
                _mains.Add(newInstance);
        }
        else
        {
            _openedInstance.CloseState(blockNumber, newStatRoot);
            if (!_openedInstance.MergeToParent() && !StateAlreadyInMain(_openedInstance)) { 
                _mains.Add(_openedInstance);
            }
        }
        _openedInstance = null;

        foreach (var mainInstance in _mains)
        {
            mainInstance.PrintStates("After close", 0);
        }
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
        NodeData? data = _openedInstance?.GetNodeDataAtRoot(rootHash, path);

        //if (data is null)
        //    return _main.GetNodeDataAtRoot(rootHash, path);

        return data;
    }

    public NodeData? GetNodeData(Span<byte> path, Keccak? hash)
    {
        NodeData? data = _openedInstance?.GetNodeData(path, hash);

        //if (data is null)
        //    return _main.GetNodeData(path, hash);

        return data;
    }

    public bool PersistUntilBlock(long blockNumber, Keccak rootHash, IBatch? batch = null)
    {
        for (int i = _mains.Count - 1; i >= 0; i--)
        {
            if (!_mains[i].PersistUntilBlock(blockNumber, rootHash, batch))
                _mains.RemoveAt(i);
        }
        return true;
    }

    public void AddRemovedPrefix(long blockNumber, ReadOnlySpan<byte> keyPrefix)
    {
        if (_openedInstance is null)
            throw new ArgumentException("No cache instance opened");

        _openedInstance.AddRemovedPrefix(blockNumber, keyPrefix);
    }

    private PathDataCacheInstance? GetMainEqualTo(PathDataCacheInstance instance)
    {
        foreach (var mainInstance in _mains)
        {
            if (mainInstance.Equals(instance)) return mainInstance;
        }
        return null;
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
}
