//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Db.FullPruning;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Crypto.Generators;

namespace Nethermind.Blockchain.FullPruning
{
    /// <summary>
    /// Main orchestrator of Full Pruning.
    /// </summary>
    /// <remarks>
    /// Without full pruning architecture is simple. <see cref="StateTree"/> uses <see cref="TrieStore"/> to store its data.
    /// <see cref="TrieStore"/> based on in memory pruning settings will from time to time save data to State DB.
    /// When saved data is considered safe for reorganisation it will announce reorg boundary of saved state to <see cref="BlockTree"/>.
    ///                                                                   
    ///                                             Announces                          
    ///     +------------+          +------------+  Reorg boundary        +------------+
    ///     |            |          |            |  (safe saved state)   |            |
    ///     | State Trie -----------> Trie Store +-----------------------> Block Tree |
    ///     |            |          |            |                       |            |
    ///     +------------+          +------|-----+                       +------------+
    ///                                    |                                           
    ///                           Persists |                                            
    ///                                    |                                           
    ///                             +------v-----+                                     
    ///                             |            |                                     
    ///                             |  State DB  |                                     
    ///                             |            |                                     
    ///                             +------------+                 
    ///
    /// When full pruning gets enabled the situation is more complicated.
    /// Between <see cref="TrieStore"/> and State DB we now have a <see cref="FullPruningDb"/>.
    /// This <see cref="FullPruningDb"/> is responsible for managing an optional new DB that will be created on demand.
    /// It will be write only. All writes to <see cref="FullPruningDb"/> will be duplicated between current DB (used for reads too) and new DB.
    /// When <see cref="IPruningTrigger"/> will be triggered, <see cref="FullPruner"/> will initiate full pruning.
    /// First it will call <see cref="FullPruningDb"/> to spawn ne DB, to mirror state into. All the written state through block processing will be mirrored.
    /// Then it will watch <see cref="BlockTree"/> for appropriate time to copy whole <see cref="StateTree"/> on safe <see cref="BlockHeader.StateRoot"/>.
    /// When safe <see cref="BlockTree.Head"/> is reached then it spawns new <see cref="CopyTreeVisitor"/> to copy the tree.
    /// When tree is copied it will commit pruning to <see cref="FullPruningDb"/>.
    /// This will switch the mirrored DB to be the current DB.
    /// Previous current DB is now useless and can be dropped for file system. This deletes all the accumulated garbage in it as we copied only recent state.
    /// 
    ///                                                            Announces                                                                                                          
    ///                +------------+          +------------+  Reorg boundry        +------------+                                                                                
    ///                |            |          |            |  (safe saved state)   |            |                                                                                
    ///                | State Trie -----------> Trie Store +-----------------------> Block Tree |                                                                                
    ///                |            |          |            |                       |            |                                                                                
    ///                +--^---------+          +------|-----+                       +------^-----+                                                                                
    ///                   |                           |                                    |                                                                                      
    ///     Crawls through                   Persists                                      |                                                                                      
    ///     whole Tree    |                           |                                    |                                                                                      
    ///                   |                   +-------v-------+                            |                                                                                      
    ///                   |                   |               |                            |                                                                                      
    ///     +-------------|---+               |FullPruningDB |                            |                                                                                      
    ///     |                 |               |               |                            |                                                                                      
    ///     | CopyTreeVisitor |        +--------------^-----------+                        |                                                                                      
    ///     |                 |        |              |           |                        |                                                                                      
    ///     ^--------|--------+        | Write only   |           | Read/Write             |                                                                                      
    ///     |        |                 |              |           |                        |                                                                                      
    ///     |        |                 |              |           |                        |                                                                                      
    ///     |        |          +------v-----+        |    +------v-----+                  |                                                                                      
    ///     |        |          | Mirrored DB|        |    | Current DB |                  |                                                                                      
    ///     |  Duplicates Tree-> State DB N+1|        |    | State DB N |                  |                                                                                      
    ///     |  into new DB      |            |        |    |            |                  |                                                                                      
    ///     |                   +------------+        |    +------------+                  |                                                                                      
    ///     |                         Commits pruning |                                    |                                                                                      
    ///     |                         Switch to new DB|                                    |                                                                                      
    ///     |                         Delete old DB   |                                    |                                                                                      
    ///     |--------------+--------------------------+                                    |                                                                                      
    ///     |              |                 Watches when it can safely start copying Trie |                                                                                      
    ///     |  FullPruner  ----------------------------------------------------------------+                                                                                      
    ///     |              |                                                                                                                                                      
    ///     +-------^------+                                                                                                                                                      
    ///             |                                                                                                                                                             
    ///             |                                                                                                                                                             
    ///             |                                                                                                                                                             
    ///    +--------|-------+                                                                                                                                                     
    ///    |                |                                                                                                                                                     
    ///    | PruningTrigger |                                                                                                                                                     
    ///    |                |                                                                                                                                                     
    ///    +----------------+ 
    /// </remarks>
    public class FullPruner : IDisposable
    {
        private readonly IFullPruningDb _fullPruningDb;
        private readonly IPruningTrigger _pruningTrigger;
        private readonly IPruningConfig _pruningConfig;
        private readonly IBlockTree _blockTree;
        private readonly IStateReader _stateReader;
        private readonly ILogManager _logManager;
        private IPruningContext? _currentPruning;
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private int _waitingForBlockProcessed = 0;
        private int _waitingForStateReady = 0;
        private long _blockToWaitFor;
        private long _stateToCopy;
        private readonly ILogger _logger;

        public FullPruner(
            IFullPruningDb fullPruningDb, 
            IPruningTrigger pruningTrigger,
            IPruningConfig pruningConfig,
            IBlockTree blockTree,
            IStateReader stateReader,
            ILogManager logManager)
        {
            _fullPruningDb = fullPruningDb;
            _pruningTrigger = pruningTrigger;
            _pruningConfig = pruningConfig;
            _blockTree = blockTree;
            _stateReader = stateReader;
            _logManager = logManager;
            _pruningTrigger.Prune += OnPrune;
            _logger = _logManager.GetClassLogger();
        }

        /// <summary>
        /// Is activated by pruning trigger, tries to start full pruning.
        /// </summary>
        private void OnPrune(object? sender, PruningEventArgs e)
        {
            // Lets assume pruning is in progress
            e.Status = PruningStatus.InProgress;
            
            // If we are already pruning, we don't need to do anything
            if (CanStartNewPruning())
            {
                // we mark that we are waiting for block (for thread safety)
                if (Interlocked.CompareExchange(ref _waitingForBlockProcessed, 1, 0) == 0)
                {
                    // we don't want to start pruning in the middle of block processing, lets wait for new head.
                    _blockTree.NewHeadBlock += OnNewHead;
                    e.Status = PruningStatus.Starting;
                }
            }
        }

        private void OnNewHead(object? sender, BlockEventArgs e)
        {
            if (CanStartNewPruning())
            {
                // mark that we are not waiting for block anymore
                if (Interlocked.CompareExchange(ref _waitingForBlockProcessed, 0, 1) == 1)
                {
                    if (e.Block is not null)
                    {
                        // try to actually start pruning
                        // this starts mirroring new writes to the mirror DB
                        if (_fullPruningDb.TryStartPruning(out IPruningContext pruningContext))
                        {
                            SetCurrentPruning(pruningContext);
                            
                            // mark that we are waiting for state to be ready 
                            if (Interlocked.CompareExchange(ref _waitingForStateReady, 1, 0) == 0)
                            {
                                _blockToWaitFor = e.Block.Number; 
                                _stateToCopy = long.MaxValue;
                                if (_logger.IsInfo) _logger.Info($"Full Pruning Ready to start: waiting for state {e.Block.Number} to be ready.");
                            }
                        }
                    }
                }
            }
            // else we already started pruning
            // if we are waiting for state to be ready
            else if (_waitingForStateReady == 1)
            {
                // if we already persisted state of the block we are waiting for
                if (_blockTree.BestPersistedState >= _blockToWaitFor && _currentPruning is not null)
                {
                    if (_stateToCopy == long.MaxValue)
                    {
                        _stateToCopy = _blockTree.BestPersistedState.Value;
                    }

                    long blockToPruneAfter = _stateToCopy + Reorganization.MaxDepth;
                    if (_blockTree.Head?.Number > blockToPruneAfter)
                    {
                        BlockHeader? header = _blockTree.FindHeader(_stateToCopy);
                        if (header is not null && Interlocked.CompareExchange(ref _waitingForStateReady, 0, 1) == 1)
                        {
                            if (_logger.IsInfo) _logger.Info($"Full Pruning Ready to start: pruning garbage before state {_stateToCopy} with root {header.StateRoot}.");
                            Task.Run(() => RunPruning(_currentPruning, header.StateRoot!));
                            _blockTree.NewHeadBlock -= OnNewHead;
                        }
                    }
                    else
                    {
                        if (_logger.IsInfo) _logger.Info($"Full Pruning Waiting for block: {blockToPruneAfter} in order to support reorganizations.");
                    }
                }
                else
                {
                    if (_logger.IsInfo) _logger.Info($"Full Pruning Waiting for state: Current best saved state {_blockTree.BestPersistedState}, waiting for saved state {_blockToWaitFor} in order to not loose any cached state.");
                }
            }
            else
            {
                _blockTree.NewHeadBlock -= OnNewHead;
            }
        }

        private void SetCurrentPruning(IPruningContext pruningContext)
        {
            IPruningContext? oldPruning = Interlocked.Exchange(ref _currentPruning, pruningContext);
            if (oldPruning is not null)
            {
                Task.Run(() => oldPruning.Dispose());
            }
        }
        
        private bool CanStartNewPruning() => _fullPruningDb.CanStartPruning;

        protected virtual void RunPruning(IPruningContext pruning, Keccak statRoot)
        {
            using (pruning)
            {
                pruning.MarkStart();
                using (CopyTreeVisitor copyTreeVisitor = new(pruning, _cancellationTokenSource, _logManager))
                {
                    VisitingOptions visitingOptions = new() {MaxDegreeOfParallelism = _pruningConfig.FullPruningMaxDegreeOfParallelism};
                    _stateReader.RunTreeVisitor(copyTreeVisitor, statRoot, visitingOptions);

                    if (!_cancellationTokenSource.IsCancellationRequested)
                    {
                        copyTreeVisitor.Finish();
                        pruning.Commit();
                    }
                }
            }
        }

        public void Dispose()
        {
            _blockTree.NewHeadBlock -= OnNewHead;
            _pruningTrigger.Prune -= OnPrune;
            _currentPruning?.Dispose();
            _cancellationTokenSource.Dispose();
        }
    }
}
