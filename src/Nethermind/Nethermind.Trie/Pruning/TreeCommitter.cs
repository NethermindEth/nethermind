using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Logging;

namespace Nethermind.Trie.Pruning
{
    public class TreeCommitter : ITreeCommitter
    {
        public TreeCommitter(IKeyValueStore keyValueStore, ILogManager logManager, long memoryLimit)
        {
            // ReSharper disable once ConstantConditionalAccessQualifier
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _keyValueStore = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));
            
            if(_logger.IsTrace)
                _logger.Trace($"Creating a new {nameof(TreeCommitter)} with memory limit {memoryLimit}");
            
            if (memoryLimit <= 0)
                throw new ArgumentOutOfRangeException(nameof(memoryLimit));

            _memoryLimit = memoryLimit;
        }

        public void Commit(long blockNumber, TrieNode trieNode)
        {
            if(_logger.IsTrace)
                _logger.Trace($"Committing {blockNumber} {trieNode}");
            
            if (blockNumber <= 0)
                throw new ArgumentOutOfRangeException(nameof(blockNumber));

            bool shouldBeginNewPackage = CurrentPackage == null || CurrentPackage.BlockNumber != blockNumber;
            if (shouldBeginNewPackage)
            {
                BeginNewPackage(blockNumber);
            }

            Debug.Assert(CurrentPackage != null, "Current package is null when enqueing a trie node.");
            CurrentPackage.Enqueue(trieNode);
        }

        public void Uncommit()
        {
            if (_queue.Count == 0)
            {
                throw new InvalidOperationException("Trying to uncommit a block when queue is empty");
            }

            _queue.RemoveLast();
        }

        // TODO: update on new package and on new trie nodes
        public long MemorySize
        {
            get => _memorySize;
            set
            {
                if (value > _memoryLimit)
                {
                    throw new InvalidOperationException(
                        $"{nameof(TreeCommitter)} exceeded memory limit of {_memoryLimit} with a value of {value}.");
                }

                _memorySize = value;
            }
        }

        #region Private

        private readonly IKeyValueStore _keyValueStore;
        
        private readonly ILogger _logger;

        private long _memorySize;

        private readonly long _memoryLimit;

        private LinkedList<BlockCommitPackage> _queue = new LinkedList<BlockCommitPackage>();

        private BlockCommitPackage? CurrentPackage => _queue.Last?.Value;

        private bool IsCurrentPackageSealed => CurrentPackage == null || CurrentPackage.IsSealed;

        private void BeginNewPackage(long blockNumber)
        {
            if(_logger.IsTrace)
                _logger.Trace($"Beginning new {nameof(BlockCommitPackage)} - {blockNumber} | memory {MemorySize}");
            
            Debug.Assert(!IsCurrentPackageSealed, "Not sealed when beginning new block");
            Debug.Assert(CurrentPackage == null || CurrentPackage.BlockNumber == blockNumber - 1,
                "Newly begun block is not a successor of the last one");

            CurrentPackage?.Seal();
            if (CurrentPackage != null)
            {
                Dispatch(CurrentPackage);
            }

            BlockCommitPackage newPackage = new BlockCommitPackage(blockNumber);
            _queue.AddLast(newPackage);

            Debug.Assert(CurrentPackage == newPackage,
                "Current package is not equal the new package just after adding");
        }

        private void Dispatch(BlockCommitPackage commitPackage)
        {
            if(_logger.IsDebug)
                _logger.Debug(
                    $"Start dispatching {nameof(BlockCommitPackage)} - {commitPackage.BlockNumber} | memory {_memorySize}");
            
            Debug.Assert(commitPackage != null && commitPackage.IsSealed,
                $"Invalid {nameof(commitPackage)} - {commitPackage} received for dispatch.");

            while (commitPackage.TryDequeue(out TrieNode currentNode))
            {
                Debug.Assert(currentNode.Keccak != null, "Committed nad has a NULL key");
                Debug.Assert(currentNode.FullRlp != null, $"Committed nad has a NULL {nameof(currentNode.FullRlp)}");

                if (currentNode.Refs == 0)
                {
                    if(_logger.IsTrace)
                        _logger.Trace($"Dropping a {nameof(TrieNode)} {currentNode} with a zero ref count.");
                    continue;
                }
                
                // if ref count is zero then just discard
                // start a batch here? (need to resolve responsibility between here and StateDb)
                if(_logger.IsTrace)
                    _logger.Trace($"Saving a {nameof(TrieNode)} {currentNode}.");
                _keyValueStore[currentNode.Keccak.Bytes] = currentNode.FullRlp;
            }
            
            if(_logger.IsDebug)
                _logger.Debug(
                    $"End dispatching {nameof(BlockCommitPackage)} - {commitPackage.BlockNumber} | memory {_memorySize}");
        }

        #endregion
    }
}