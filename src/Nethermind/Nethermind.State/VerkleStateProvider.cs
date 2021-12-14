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
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Resettables;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using System.Runtime.CompilerServices;
using Metrics = Nethermind.Db.Metrics;


[assembly: InternalsVisibleTo("Ethereum.Test.Base")]
[assembly: InternalsVisibleTo("Ethereum.Blockchain.Test")]
[assembly: InternalsVisibleTo("Nethermind.State.Test")]
[assembly: InternalsVisibleTo("Nethermind.Benchmark")]
[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]
[assembly: InternalsVisibleTo("Nethermind.Synchronization.Test")]

namespace Nethermind.State
{
    public class VerkleStateProvider
    {
        private const int StartCapacity = Resettable.StartCapacity;
        private readonly ResettableDictionary<Address, Stack<int>> _intraBlockCache = new();
        private readonly ResettableHashSet<Address> _committedThisRound = new();

        private readonly List<Change> _keptInCache = new();
        private readonly ILogger _logger;
        private readonly VerkleStateTree _tree;
        
        private int _capacity = StartCapacity;
        private Change?[] _changes = new Change?[StartCapacity];
        private int _currentPosition = Resettable.EmptyPosition;
        
        private readonly HashSet<Address> _readsForTracing = new();
        private bool _needsStateRootUpdate;
        
        
        
        public VerkleStateProvider(ILogManager? logManager)
        {
            _tree = new VerkleStateTree(logManager);
            _logger = logManager?.GetClassLogger<StateProvider>() ?? throw new ArgumentNullException(nameof(logManager));
            // TODO: move this calculation out of here
            
        }
        
        private Account? GetState(Address address)
        {
            Metrics.StateTreeReads++;
            Account? account = _tree.Get(address);
            return account;
        }

        private void SetState(Address address, Account? account)
        {
            _needsStateRootUpdate = true;
            Metrics.StateTreeWrites++;
            _tree.Set(address, account);
        }

        private void PushNew(Address address, Account account)
        {
            SetupCache(address);
            IncrementChangePosition();
            _intraBlockCache[address].Push(_currentPosition);
            _changes[_currentPosition] = new Change(ChangeType.New, address, account);
        }
        
        private void IncrementChangePosition()
        {
            Resettable<Change>.IncrementPosition(ref _changes, ref _capacity, ref _currentPosition);
        }
        
        private void SetupCache(Address address)
        {
            if (!_intraBlockCache.ContainsKey(address))
            {
                _intraBlockCache[address] = new Stack<int>();
            }
        }
        
        
        
        
        private enum ChangeType
        {
            JustCache,
            Touch,
            Update,
            New,
            Delete
        }
        private class Change
        {
            public Change(ChangeType type, Address address, Account? account)
            {
                ChangeType = type;
                Address = address;
                Account = account;
            }

            public ChangeType ChangeType { get; }
            public Address Address { get; }
            public Account? Account { get; }
        }
        
        public void Reset()
        {
            if (_logger.IsTrace) _logger.Trace("Clearing state provider caches");
            _intraBlockCache.Reset();
            _committedThisRound.Reset();
            _readsForTracing.Clear();
            _currentPosition = Resettable.EmptyPosition;
            Array.Clear(_changes, 0, _changes.Length);
            _needsStateRootUpdate = false;
        }

        // public void CommitTree(long blockNumber)
        // {
        //     if (_needsStateRootUpdate)
        //     {
        //         RecalculateStateRoot();
        //     }
        //
        //     _tree.Commit(blockNumber);
        // }
        
    }
    
}
