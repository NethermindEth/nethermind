using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Trie.Pruning
{
    // is this class even needed?
    // we need to stack ref changes for each block
    public class TreeCommitter : ITreeCommitter
    {
        public TreeCommitter(
            IKeyValueStore keyValueStore,
            ILogManager logManager,
            long memoryLimit,
            long lookupLimit = 128)
        {
            // ReSharper disable once ConstantConditionalAccessQualifier
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _keyValueStore = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));

            if (_logger.IsTrace)
                _logger.Trace($"Creating a new {nameof(TreeCommitter)} with memory limit {memoryLimit}");

            if (memoryLimit <= 0)
                throw new ArgumentOutOfRangeException(nameof(memoryLimit));
            if (lookupLimit <= 0)
                throw new ArgumentOutOfRangeException(nameof(lookupLimit));

            _memoryLimit = memoryLimit;
            _lookupLimit = lookupLimit;

            MemorySize =
                MemorySizes.SmallObjectOverhead +
                5 * MemorySizes.RefSize -
                MemorySizes.SmallObjectFreeDataSize +
                40 /* linked list */;
        }

        public bool IsInMemory(Keccak key)
        {
            return _inMemNodes.ContainsKey(key);
        }

        public void Commit(long blockNumber, TrieNode? trieNode)
        {
            if (_logger.IsTrace)
                _logger.Trace($"Committing {blockNumber} {trieNode}");

            if (blockNumber < 0)
                throw new ArgumentOutOfRangeException(nameof(blockNumber));

            bool shouldBeginNewPackage = CurrentPackage == null || CurrentPackage.BlockNumber != blockNumber;
            if (shouldBeginNewPackage)
            {
                BeginNewPackage(blockNumber);
            }

            _currentRoot = trieNode ?? _currentRoot;

            if (trieNode != null)
            {
                _commitCount++;

                if (trieNode.Keccak == null)
                {
                    throw new InvalidOperationException(
                        $"Hash of the node {trieNode} should be known at the time of committing.");
                }

                Debug.Assert(CurrentPackage != null, "Current package is null when enqueing a trie node.");

                if (_inMemNodes.ContainsKey(trieNode.Keccak))
                {
                    // TODO: check if this solves the checklist problem
                    // _inMemNodes[trieNode.Keccak].Refs += trieNode.Refs;
                }
                else
                {
                    long previousPackageMemory = CurrentPackage.MemorySize;
                    CurrentPackage.Enqueue(trieNode);
                    _inMemNodes[trieNode.Keccak] = trieNode;
                    AddToMemory(CurrentPackage.MemorySize - previousPackageMemory);
                }
            }
        }

        public void Uncommit()
        {
            if (_queue.Count == 0)
            {
                throw new InvalidOperationException("Trying to uncommit a block when queue is empty");
            }

            _queue.RemoveLast();
        }

        public void Flush()
        {
            if (_logger.IsDebug)
                _logger.Debug($"Flushing trie cache - in memory {_inMemNodes.Count} " +
                              $"| commit count {_commitCount} " +
                              $"| drop count {_dropCount} " +
                              $"| carry count {_carriedCount} " +
                              $"| save count {_saveCount}");

            CurrentPackage?.Seal();
            while (TryDispatchOne())
            {
            }

            if (_logger.IsDebug)
                _logger.Debug($"Flushed trie cache - in memory {_inMemNodes.Count} " +
                              $"| commit count {_commitCount} " +
                              $"| drop count {_dropCount} " +
                              $"| carry count {_carriedCount} " +
                              $"| save count {_saveCount}");
        }

        public byte[] this[byte[] key] => _keyValueStore[key];

        public TrieNode FindCached(Keccak key)
        {
            return _inMemNodes.TryGetValue(key, out TrieNode trieNode) ? trieNode : null;
        }

        public long MemorySize { get; private set; }

        #region Private

        private const int LinkedListNodeMemorySize = 48;

        private readonly IKeyValueStore _keyValueStore;

        private readonly ILogger _logger;

        private readonly long _memoryLimit;

        private readonly long _lookupLimit;

        private long _highestBlockNumber;

        private LinkedList<BlockCommitPackage> _queue = new LinkedList<BlockCommitPackage>();

        private Queue<TrieNode> _carryQueue = new Queue<TrieNode>();

        private Dictionary<Keccak, TrieNode> _inMemNodes = new Dictionary<Keccak, TrieNode>();

        private BlockCommitPackage? CurrentPackage => _queue.Last?.Value;

        private bool IsCurrentPackageSealed => CurrentPackage == null || CurrentPackage.IsSealed;

        private Stack<TrieNode> _rootStack = new Stack<TrieNode>();

        private TrieNode _currentRoot;

        private void BeginNewPackage(long blockNumber)
        {
            if (_logger.IsDebug)
                _logger.Debug($"Beginning new {nameof(BlockCommitPackage)} - {blockNumber} | memory {MemorySize}");

            Debug.Assert(CurrentPackage == null || CurrentPackage.BlockNumber == blockNumber - 1,
                "Newly begun block is not a successor of the last one");

            CurrentPackage?.Seal();
            if (_currentRoot != null)
            {
                _rootStack.Push(_currentRoot);
                _currentRoot.IncrementRefsRecursively();
            }

            Debug.Assert(IsCurrentPackageSealed, "Not sealed when beginning new block");

            BlockCommitPackage newPackage = new BlockCommitPackage(blockNumber);
            _queue.AddLast(newPackage);
            
            _highestBlockNumber = Math.Max(blockNumber, _highestBlockNumber);
            if (_queue.First!.Value.BlockNumber <= _highestBlockNumber - _lookupLimit)
            {
                TryDispatchOne();
            }

            long newMemory = newPackage.MemorySize + LinkedListNodeMemorySize;
            AddToMemory(newMemory);

            Debug.Assert(CurrentPackage == newPackage,
                "Current package is not equal the new package just after adding");
        }

        private void AddToMemory(long newMemory)
        {
            while (MemorySize + newMemory > _memoryLimit)
            {
                bool success = TryDispatchOne();
                if (!success)
                {
                    break;
                }
            }

            if (MemorySize + newMemory > _memoryLimit)
            {
                if (_logger.IsTrace)
                    _logger.Trace($"Not able to dispatch to decrease memory usage below the limit of {_memoryLimit}.");
            }

            MemorySize += newMemory;
        }

        private bool TryDispatchOne()
        {
            BlockCommitPackage package = _queue.First?.Value;
            bool canDispatch = package?.IsSealed ?? false;
            if (canDispatch)
            {
                Dispatch(package);
                _queue.RemoveFirst();
            }

            return canDispatch;
        }

        private int _commitCount;

        private int _dropCount;

        private int _carriedCount;

        private int _saveCount;

        private void Dispatch(BlockCommitPackage commitPackage)
        {
            if (_logger.IsDebug)
                _logger.Debug(
                    $"Start dispatching {nameof(BlockCommitPackage)} - {commitPackage.BlockNumber} | memory {MemorySize}");

            Debug.Assert(commitPackage != null && commitPackage.IsSealed,
                $"Invalid {nameof(commitPackage)} - {commitPackage} received for dispatch.");

            long memoryToDrop = commitPackage.MemorySize + LinkedListNodeMemorySize;

            TrieNode root = null;
            Queue<TrieNode> localCarryQueue = _carryQueue;
            _carryQueue = new Queue<TrieNode>();
            while (localCarryQueue.TryDequeue(out TrieNode currentNode) ||
                   commitPackage.TryDequeue(out currentNode))
            {
                root = currentNode; // root will be the last one
                Debug.Assert(currentNode.Keccak != null, $"Committed node {currentNode} has a NULL key");
                Debug.Assert(currentNode.FullRlp != null, $"Committed node {currentNode} has a NULL {nameof(currentNode.FullRlp)}");

                // if ref count is zero then just discard
                bool isSnapshotBlock = commitPackage.BlockNumber % _lookupLimit == 0;
                if (currentNode.Refs == 0)
                {
                    if (!currentNode.IsPersisted)
                    {
                        throw new TrieException(
                            $"Refs should never be zero while committing from a package queue ({currentNode})");    
                    }
                    else
                    {
                        continue;
                    }
                    
                    // this must have been carried and so there are no snapshot roots holding it
                    // TODO: Assert that it is persisted
                }

                if (isSnapshotBlock)
                {
                    if (_logger.IsTrace)
                        _logger.Trace($"Saving a {nameof(TrieNode)} {currentNode}.");
                    _keyValueStore[currentNode.Keccak.Bytes] = currentNode.FullRlp;
                    _inMemNodes.Remove(currentNode.Keccak);
                    currentNode.IsPersisted = true;
                    _saveCount++;
                }
                else
                {
                    if (currentNode.Refs == 1) // only referenced by this root
                    {
                        if (_logger.IsTrace)
                            _logger.Trace($"Dropping a {nameof(TrieNode)} {currentNode}.");
                        _inMemNodes.Remove(currentNode.Keccak);
                        _dropCount++;
                    }
                    else
                    {
                        if (_logger.IsTrace)
                            _logger.Trace($"Carrying a {nameof(TrieNode)} {currentNode}.");
                        // TODO: this will carry even saved ones which may lead to a massive mem leak
                        // we need to be able to drop the refs for saved nodes, maybe in mem review and unloading
                        // all trie nodes marked as persisted?
                        _carryQueue.Enqueue(currentNode);
                        _carriedCount++;
                    }     
                }
            }

            // TODO: so here we HAD a big problem of same nodes represented multiple times as .NET objects and having mismatched refs
            if (root != null && root.Refs != 0)
            {
                // remove from memory on zero reference?
                root.DecrementRefsRecursively();
            }

            MemorySize -= memoryToDrop;
            if (_logger.IsDebug)
                _logger.Debug(
                    $"End dispatching {nameof(BlockCommitPackage)} - {commitPackage.BlockNumber} | memory {MemorySize}");
        }

        #endregion
    }
}