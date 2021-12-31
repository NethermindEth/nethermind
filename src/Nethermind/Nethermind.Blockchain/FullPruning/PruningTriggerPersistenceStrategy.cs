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
using System.Threading;
using Nethermind.Core;
using Nethermind.Db.FullPruning;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.Blockchain.FullPruning;

/// <summary>
/// Forces to persist state trie to DB after next block of full pruning trigger <see cref="IPruningTrigger.Prune"/>.
/// </summary>
/// <remarks>
/// Used when both memory pruning and full pruning are enabled.
/// We need to store state trie to DB to be able to copy this trie into new database in full pruning.
/// </remarks>
public class PruningTriggerPersistenceStrategy : IPersistenceStrategy, IDisposable
{
    private readonly IFullPruningDb _fullPruningDb;
    private int _inPruning = 0;

    public PruningTriggerPersistenceStrategy(IFullPruningDb fullPruningDb)
    {
        _fullPruningDb = fullPruningDb;
        _fullPruningDb.PruningFinished += OnPruningFinished;
        _fullPruningDb.PruningStarted += OnPruningStarted;
    }

    private void OnPruningFinished(object? sender, EventArgs e)
    {
        Interlocked.CompareExchange(ref _inPruning, 0, 1);
    }

    private void OnPruningStarted(object? sender, EventArgs e)
    {
        Interlocked.CompareExchange(ref _inPruning, 1, 0);
    }

    public bool ShouldPersist(long blockNumber) => _inPruning != 0;

    /// <inheritdoc/>
    public void Dispose()
    {
        _fullPruningDb.PruningStarted -= OnPruningStarted;
        _fullPruningDb.PruningFinished -= OnPruningFinished;
    }
}
