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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Executor;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.Access;
using Nethermind.State;
using Nethermind.TxPool.Collections;

namespace Nethermind.AccountAbstraction.Source
{
    public class UserOperationPool : IUserOperationPool
    {
        private readonly IBlockTree _blockTree;
        private readonly IStateProvider _stateProvider;
        private readonly IBlockchainProcessor _blockchainProcessor;
        private readonly UserOperationSortedPool _userOperationSortedPool;
        private readonly IUserOperationSimulator _userOperationSimulator;
        private readonly ISimulatedUserOperationSource _simulatedUserOperationSource;
        private readonly ConcurrentDictionary<UserOperation, SimulatedUserOperationContext> _simulatedUserOperations;

        public UserOperationPool(
            IBlockTree blockTree, 
            IStateProvider stateProvider, 
            IBlockchainProcessor blockchainProcessor, 
            UserOperationSortedPool userOperationSortedPool, 
            IUserOperationSimulator userOperationSimulator,
            ISimulatedUserOperationSource simulatedUserOperationSource,
            ConcurrentDictionary<UserOperation, SimulatedUserOperationContext> simulatedUserOperations)
        {
            _blockTree = blockTree;
            _stateProvider = stateProvider;
            _blockchainProcessor = blockchainProcessor;
            _userOperationSortedPool = userOperationSortedPool;
            _userOperationSimulator = userOperationSimulator;
            _simulatedUserOperationSource = simulatedUserOperationSource;
            _simulatedUserOperations = simulatedUserOperations;

            blockTree.NewHeadBlock += OnNewBlock;
            _userOperationSortedPool.Removed += UserOperationRemoved;
        }

        private void UserOperationRemoved(object? sender, SortedPool<UserOperation, UserOperation, Address>.SortedPoolRemovedEventArgs e)
        {
            UserOperation userOperation = e.Key;
            _simulatedUserOperations.TryRemove(userOperation, out _);
        }

        private void OnNewBlock(object? sender, BlockEventArgs e)
        {
            Block block = e.Block;
            AccessBlockTracer accessBlockTracer = new(Array.Empty<Address>());
            ITracer tracer = new Tracer(_stateProvider, _blockchainProcessor);
            tracer.Trace(block, accessBlockTracer);

            IEnumerable<SimulatedUserOperation> simulatedUserOperations = _simulatedUserOperationSource.GetSimulatedUserOperations();
            // verify each one still has enough balance, nonce is correct etc.

        }

        public IEnumerable<UserOperation> GetUserOperations() => _userOperationSortedPool.GetSnapshot();

        public bool AddUserOperation(UserOperation userOperation)
        {
            if (ValidateUserOperation(userOperation))
            {
                return _userOperationSortedPool.TryInsert(userOperation, userOperation);
            }
            return false;
        }

        private bool ValidateUserOperation(UserOperation userOperation)
        {
            // make sure all fields present
            // make sure all field values make sense
            // make sure signature is correct
            return true;
        }
    }
}
