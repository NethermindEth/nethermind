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
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Crypto.Generators;

namespace Nethermind.Blockchain.FullPruning
{
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
        private long _stateToWaitFor;
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

        private void OnPrune(object? sender, PruningEventArgs e)
        {
            if (CanRunPruning())
            {
                if (Interlocked.CompareExchange(ref _waitingForBlockProcessed, 1, 0) == 0)
                {
                    // we don't want to start pruning in the middle of block processing.
                    _blockTree.NewHeadBlock += OnNewHead;
                    e.Status = PruningStatus.Starting;
                }
                else
                {
                    e.Status = PruningStatus.AlreadyInProgress;
                }
            }
            else
            {
                e.Status = PruningStatus.AlreadyInProgress;                
            }
        }

        private void OnNewHead(object? sender, BlockEventArgs e)
        {
            if (CanRunPruning())
            {
                if (Interlocked.CompareExchange(ref _waitingForBlockProcessed, 0, 1) == 1)
                {
                    if (e.Block is not null)
                    {
                        if (_fullPruningDb.TryStartPruning(out IPruningContext pruningContext))
                        {
                            SetCurrentPruning(pruningContext);
                            //bool withMemPruning = (_pruningConfig.Mode & PruningMode.Memory) != 0;
                            // if (!withMemPruning)
                            // {
                            //     if (e.Block.StateRoot is not null)
                            //     {
                            //         _blockTree.NewHeadBlock -= OnNewHead;
                            //         Task.Run(() => RunPruning(pruningContext, e.Block.StateRoot));
                            //     }
                            // }
                            if (Interlocked.CompareExchange(ref _waitingForStateReady, 1, 0) == 0)
                            {
                                _stateToWaitFor = e.Block.Number;
                                if (_logger.IsInfo) _logger.Info($"Full Pruning Ready to start: waiting for state {e.Block.Number} to be ready.");
                            }
                        }
                    }
                }
            }
            else if (_waitingForStateReady == 1)
            {
                if (_blockTree.BestState >= _stateToWaitFor && _currentPruning is not null)
                {
                    BlockHeader? header = _blockTree.FindHeader(_blockTree.BestState.Value);
                    if (header is not null && Interlocked.CompareExchange(ref _waitingForStateReady, 0, 1) == 1)
                    {
                        if (_logger.IsInfo) _logger.Info($"Full Pruning Ready to start: pruning garbage before state {_blockTree.BestState}.");
                        Task.Run(() => RunPruning(_currentPruning, header.StateRoot!));
                        _blockTree.NewHeadBlock -= OnNewHead;
                    }
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

        private bool CanRunPruning() => _fullPruningDb.CanStartPruning;

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
