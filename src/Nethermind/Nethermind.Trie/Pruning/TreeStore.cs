using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Trie.Pruning
{
    public class TreeStore : ITreeStore
    {
        public TreeStore(
            ITrieNodeCache trieNodeCache,
            IKeyValueStore keyValueStore,
            ILogManager logManager,
            long memoryLimit,
            long lookupLimit = 128)
        {
            // ReSharper disable once ConstantConditionalAccessQualifier
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _trieNodeCache = trieNodeCache ?? throw new ArgumentNullException(nameof(trieNodeCache));
            _keyValueStore = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));

            if (_logger.IsTrace)
                _logger.Trace($"Creating a new {nameof(TreeStore)} with memory limit {memoryLimit}");

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

        /// <summary>
        /// Beware - at the moment we assume a strict leaf to root ordering of commits
        /// </summary>
        /// <param name="blockNumber"></param>
        /// <param name="nodeCommitInfo"></param>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        /// <exception cref="InvalidOperationException"></exception>
        public void Commit(long blockNumber, NodeCommitInfo nodeCommitInfo)
        {
            if (blockNumber < 0)
                throw new ArgumentOutOfRangeException(nameof(blockNumber));

            bool shouldBeginNewPackage = CurrentPackage == null || CurrentPackage.BlockNumber != blockNumber;
            if (shouldBeginNewPackage)
            {
                BeginNewPackage(blockNumber);
            }

            if (_logger.IsTrace)
                _logger.Trace($"Committing {blockNumber} {nodeCommitInfo}");

            if (!nodeCommitInfo.IsEmptyBlockMarker)
            {
                TrieNode trieNode = nodeCommitInfo.Node;
                _commitCount++;

                if (trieNode!.Keccak == null)
                {
                    throw new InvalidOperationException(
                        $"Hash of the node {trieNode} should be known at the time of committing.");
                }

                Debug.Assert(CurrentPackage != null, "Current package is null when enqueing a trie node.");

                if (_trieNodeCache.IsInMemory(trieNode.Keccak))
                {
                    TrieNode cachedReplacement = _trieNodeCache.Get(trieNode.Keccak);
                    if (!ReferenceEquals(cachedReplacement, trieNode))
                    {
                        if (_logger.IsTrace)
                            _logger.Trace(
                                $"Replacing a {nameof(trieNode)} object {trieNode} with its cached representation {cachedReplacement}.");
                        if (!nodeCommitInfo.IsRoot)
                        {
                            nodeCommitInfo.NodeParent!.ReplaceChildRef(nodeCommitInfo.ChildPositionAtParent, cachedReplacement);
                        }
                    }
                }
                else
                {
                    long previousPackageMemory = CurrentPackage.MemorySize;
                    CurrentPackage.Enqueue(trieNode);
                    _trieNodeCache.Set(trieNode.Keccak, trieNode);
                    AddToMemory(CurrentPackage.MemorySize - previousPackageMemory);
                }
            }

            _trieNodeCache.Dump();
        }

        public bool IsInMemory(Keccak keccak)
        {
            return _trieNodeCache.IsInMemory(keccak);
        }

        public void FinishBlockCommit(long blockNumber, TrieNode? root)
        {
            bool shouldBeginNewPackage = CurrentPackage == null || CurrentPackage.BlockNumber != blockNumber;
            if (shouldBeginNewPackage)
            {
                BeginNewPackage(blockNumber);
            }

            if (CurrentPackage != null)
            {
                CurrentPackage.Root = root;
                if (_logger.IsTrace)
                    _logger.Trace(
                        $"Current root (block {blockNumber}): {CurrentPackage.Root}, block {CurrentPackage?.BlockNumber}");
                if (_logger.IsTrace)
                    _logger.Trace(
                        $"Incrementing refs from block {blockNumber} root {CurrentPackage.Root?.ToString() ?? "NULL"} ");
                CurrentPackage.Root?.IncrementRefsRecursively(CurrentPackage.BlockNumber);
                CurrentPackage.Seal();

                _trieNodeCache.Dump();
            }
        }

        public void Unwind()
        {
            if(!_packageQueue.Any())
            {
                throw new InvalidOperationException(
                    $"Trying to unwind a {nameof(BlockCommitPackage)} when the queue is empty.");
            }

            DecrementRefs(CurrentPackage);
            _packageQueue.RemoveLast();
        }

        public void Flush()
        {
            if (_logger.IsDebug)
                _logger.Debug($"Flushing trie cache - in memory {_trieNodeCache.Count} " +
                              $"| commit count {_commitCount} " +
                              $"| drop count {_dropCount} " +
                              $"| carry count {_carriedCount} " +
                              $"| save count {_saveCount}");

            CurrentPackage?.Seal();
            while (TryDispatchOne())
            {
            }

            if (_logger.IsDebug)
                _logger.Debug($"Flushed trie cache - in memory {_trieNodeCache.Count} " +
                              $"| commit count {_commitCount} " +
                              $"| drop count {_dropCount} " +
                              $"| carry count {_carriedCount} " +
                              $"| save count {_saveCount}");
        }

        public TrieNode? FindCachedOrUnknown(Keccak hash)
        {
            return _trieNodeCache.Get(hash);
        }

        public byte[]? LoadRlp(Keccak keccak, bool allowCaching)
        {
            if (!allowCaching)
            {
                return _keyValueStore[keccak.Bytes];
            }

            // TODO: to not modify too many other parts of the solution I leave the cache as a static field of
            // the PatriciaTree class for now
            byte[] cachedRlp = PatriciaTree.NodeCache.Get(keccak);
            if (cachedRlp == null)
            {
                byte[] dbValue = _keyValueStore[keccak.Bytes];
                if (dbValue == null)
                {
                    throw new TrieException($"Node {keccak} is missing from the DB");
                }

                PatriciaTree.NodeCache.Set(keccak, dbValue);
                return dbValue;
            }

            return cachedRlp;
        }

        public long MemorySize { get; private set; }

        #region Private

        private const int LinkedListNodeMemorySize = 48;

        private readonly ITrieNodeCache _trieNodeCache;

        private readonly IKeyValueStore _keyValueStore;

        private readonly ILogger _logger;

        private readonly long _memoryLimit;

        private readonly long _lookupLimit;

        private int _commitCount;

        private int _dropCount;

        private int _carriedCount;

        private int _saveCount;

        private long _highestBlockNumber;

        private LinkedList<BlockCommitPackage> _packageQueue = new LinkedList<BlockCommitPackage>();

        private Queue<TrieNode> _carryQueue = new Queue<TrieNode>();

        private BlockCommitPackage? CurrentPackage => _packageQueue.Last?.Value;

        private bool IsCurrentPackageSealed => CurrentPackage == null || CurrentPackage.IsSealed;

        private void BeginNewPackage(long blockNumber)
        {
            if (_logger.IsDebug)
                _logger.Debug($"Beginning new {nameof(BlockCommitPackage)} - {blockNumber} | memory {MemorySize}");

            Debug.Assert(CurrentPackage == null || CurrentPackage.BlockNumber == blockNumber - 1,
                "Newly begun block is not a successor of the last one");

            Debug.Assert(IsCurrentPackageSealed, "Not sealed when beginning new block");

            BlockCommitPackage newPackage = new BlockCommitPackage(blockNumber);
            _packageQueue.AddLast(newPackage);

            _highestBlockNumber = Math.Max(blockNumber, _highestBlockNumber);
            if (_packageQueue.First!.Value.BlockNumber <= _highestBlockNumber - _lookupLimit)
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
            BlockCommitPackage package = _packageQueue.First?.Value;
            bool canDispatch = package?.IsSealed ?? false;
            if (canDispatch)
            {
                Dispatch(package);
                _packageQueue.RemoveFirst();
            }

            return canDispatch;
        }

        private void Dispatch(BlockCommitPackage commitPackage)
        {
            if (_logger.IsDebug)
                _logger.Debug(
                    $"Start dispatching {nameof(BlockCommitPackage)} - {commitPackage.BlockNumber} | memory {MemorySize}");

            Debug.Assert(commitPackage != null && commitPackage.IsSealed,
                $"Invalid {nameof(commitPackage)} - {commitPackage} received for dispatch.");

            long memoryToDrop = commitPackage.MemorySize + LinkedListNodeMemorySize;

            Queue<TrieNode> localCarryQueue = _carryQueue;
            _carryQueue = new Queue<TrieNode>();

            while (localCarryQueue.TryDequeue(out TrieNode currentNode) ||
                   commitPackage.TryDequeue(out currentNode))
            {
                Debug.Assert(
                    currentNode.Keccak != null,
                    $"Committed node {currentNode} has a NULL key");
                Debug.Assert(
                    currentNode.FullRlp != null,
                    $"Committed node {currentNode} has a NULL {nameof(currentNode.FullRlp)}");

                if (currentNode.Refs == 0)
                {
                    DropNode(currentNode);
                }
                else
                {
                    bool isSnapshotBlock = commitPackage.BlockNumber % _lookupLimit == 0;
                    if (isSnapshotBlock)
                    {
                        if (!currentNode.IsPersisted)
                        {
                            if (_logger.IsTrace)
                                _logger.Trace($"Saving a {nameof(TrieNode)} {currentNode}.");
                            _keyValueStore[currentNode.Keccak.Bytes] = currentNode.FullRlp;
                            _trieNodeCache.Remove(currentNode.Keccak);
                            currentNode.IsPersisted = true;
                            _saveCount++;
                        }
                    }
                    else
                    {
                        if (currentNode.Refs <= 1)
                        {
                            // since it is only referenced by this block, it can be dropped
                            DropNode(currentNode);
                        }
                        else
                        {
                            if (_logger.IsTrace)
                                _logger.Trace($"Carrying a {nameof(TrieNode)} {currentNode}.");

                            if (!currentNode.IsPersisted)
                            {
                                _carryQueue.Enqueue(currentNode);
                                _carriedCount++;
                            }
                        }
                    }
                }
            }

            DecrementRefs(commitPackage);

            MemorySize -= memoryToDrop;
            if (_logger.IsDebug)
                _logger.Debug(
                    $"End dispatching {nameof(BlockCommitPackage)} - {commitPackage.BlockNumber} | memory {MemorySize}");

            _trieNodeCache.Dump();
        }

        private void DropNode(TrieNode trieNode)
        {
            if (_logger.IsTrace)
                _logger.Trace($"Dropping a {nameof(TrieNode)} {trieNode}.");
            _trieNodeCache.Remove(trieNode.Keccak);
            _dropCount++;
        }

        private void DecrementRefs(BlockCommitPackage package)
        {
            if (!package.IsSealed)
            {
                throw new InvalidOperationException(
                    $"Current {nameof(BlockCommitPackage)} has to be sealed before the refs can be decremented.");
            }

            TrieNode? root = package.Root;
            if (_logger.IsTrace)
                _logger.Trace(
                    $"Decrementing refs from block {package.BlockNumber} root {root?.ToString() ?? "NULL"}");

            if (root != null)
            {
                if (root.Refs == 0)
                {
                    throw new InvalidDataException($"Found root node {root} with 0 refs.");
                }

                root.DecrementRefsRecursively();
                _trieNodeCache.Prune();
                _trieNodeCache.Dump();
            }
        }

        #endregion
    }
}
