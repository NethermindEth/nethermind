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
using Nethermind.Core;
using Nethermind.Db.FullPruning;
using Nethermind.State;
using Org.BouncyCastle.Crypto.Generators;

namespace Nethermind.Blockchain.FullPruning
{
    public class FullPruner : IDisposable
    {
        private readonly IFullPruningDb _fullPruningDb;
        private readonly IPruningTrigger _pruningTrigger;
        private readonly IBlockTree _blockTree;
        private readonly IStateReader _stateReader;
        private IPruningContext? _currentPruning;
        private readonly CancellationTokenSource _cancellationTokenSource = new();

        public FullPruner(
            IFullPruningDb fullPruningDb, 
            IPruningTrigger pruningTrigger,
            IBlockTree blockTree,
            IStateReader stateReader)
        {
            _fullPruningDb = fullPruningDb;
            _pruningTrigger = pruningTrigger;
            _blockTree = blockTree;
            _stateReader = stateReader;
            _pruningTrigger.Prune += OnPrune;
        }

        private void OnPrune(object? sender, EventArgs e)
        {
            if (_blockTree.HighestPersistedState.HasValue)
            {
                BlockHeader? header = _blockTree.FindHeader(_blockTree.HighestPersistedState.Value);
                if (header is not null)
                {
                    if (_fullPruningDb.TryStartPruning(out IPruningContext pruningContext))
                    {
                        Task.Run(() => RunPruning(pruningContext, header));
                    }
                }
            }
        }

        private void RunPruning(IPruningContext pruningContext, BlockHeader header)
        {
            using (_currentPruning)
            {
                IPruningContext? oldPruning = Interlocked.Exchange(ref _currentPruning, pruningContext);
                oldPruning?.Dispose();

                CopyTreeVisitor copyTreeVisitor = new(_currentPruning, _cancellationTokenSource.Token);
                _stateReader.RunTreeVisitor(copyTreeVisitor, header.StateRoot!);

                if (!_cancellationTokenSource.IsCancellationRequested)
                {
                    _currentPruning.Commit();
                }
            }
        }

        public void Dispose()
        {
            _pruningTrigger.Prune -= OnPrune;
            _currentPruning?.Dispose();
            _cancellationTokenSource.Dispose();
        }
    }
}
