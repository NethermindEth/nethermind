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
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Nethermind.AccountAbstraction.Broadcaster;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Executor;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.State;
using Nethermind.Stats.Model;
using Nethermind.TxPool.Collections;

namespace Nethermind.AccountAbstraction.Source
{
    public class UserOperationPool : IUserOperationPool
    {
        private readonly IBlockTree _blockTree;
        private readonly IStateProvider _stateProvider;
        private readonly ITimestamper _timestamper;
        private readonly IAccountAbstractionConfig _accountAbstractionConfig;
        private readonly ISet<Address> _bannedPaymasters;
        private readonly UserOperationSortedPool _userOperationSortedPool;
        private readonly IUserOperationSimulator _userOperationSimulator;
        private readonly IPeerManager _peerManager;
        //private readonly UserOperationBroadcaster _broadcaster;

        public UserOperationPool(IBlockTree blockTree,
            IStateProvider stateProvider,
            ITimestamper timestamper,
            IAccountAbstractionConfig accountAbstractionConfig,
            ISet<Address> bannedPaymasters,
            IPeerManager peerManager,
            UserOperationSortedPool userOperationSortedPool,
            IUserOperationSimulator userOperationSimulator)
        {
            _blockTree = blockTree;
            _stateProvider = stateProvider;
            _timestamper = timestamper;
            _accountAbstractionConfig = accountAbstractionConfig;
            _bannedPaymasters = bannedPaymasters;
            _peerManager = peerManager;
            _userOperationSortedPool = userOperationSortedPool;
            _userOperationSimulator = userOperationSimulator;

            _userOperationSortedPool.Inserted += UserOperationInserted;
        }
        
        private void UserOperationInserted(object? sender, SortedPool<UserOperation, UserOperation, Address>.SortedPoolEventArgs e)
        {
            UserOperation userOperation = e.Key;
            BroadcastToCompatiblePeers(userOperation, _peerManager.ConnectedPeers);
        }

        private void BroadcastToCompatiblePeers(UserOperation userOperation, IReadOnlyCollection<Peer> peers)
        {
            Capability? aaCapability = new Capability(Protocol.AA, 0);
            IEnumerable<Peer> compatiblePeers = peers.Where(peer => peer.OutSession!.HasAgreedCapability(aaCapability));
            Task.Run(() =>
            {
                //_broadcaster.BroadcastOnce(userOperation);
            });
        }

        public IEnumerable<UserOperation> GetUserOperations() => _userOperationSortedPool.GetSnapshot();

        public ResultWrapper<Keccak> AddUserOperation(UserOperation userOperation)
        {
            ResultWrapper<Keccak> result = ValidateUserOperation(userOperation);
            if (result.Result == Result.Success)
            {
                if (_userOperationSortedPool.TryInsert(userOperation, userOperation))
                {
                    return ResultWrapper<Keccak>.Success(userOperation.Hash);
                }
                else
                {
                    return ResultWrapper<Keccak>.Fail("failed to insert userOp into pool");
                }
            }
            
            return result;
        }

        public bool RemoveUserOperation(UserOperation userOperation)
        {
            return _userOperationSortedPool.TryRemove(userOperation);
        }

        public AddUserOperationResult SubmitUserOperation(UserOperation userOperation, UserOperationHandlingOptions handlingOptions)
        {
            throw new NotImplementedException();
        }

        private ResultWrapper<Keccak> ValidateUserOperation(UserOperation userOperation)
        {
            if (userOperation.MaxFeePerGas < _accountAbstractionConfig.MinimumGasPrice)
            {
                return ResultWrapper<Keccak>.Fail("maxFeePerGas below minimum gas price");
            }

            if (userOperation.CallGas < Transaction.BaseTxGasCost)
            {
                return ResultWrapper<Keccak>.Fail($"callGas too low, must be at least {Transaction.BaseTxGasCost}");
            }

            // make sure target account exists
            if (
                userOperation.Sender == Address.Zero
                || !(_stateProvider.AccountExists(userOperation.Sender) || userOperation.InitCode != Bytes.Empty))
            {
                return ResultWrapper<Keccak>.Fail("sender doesn't exist");
            }

            // make sure paymaster is a contract (if paymaster is used) and is not on banned list
            if (userOperation.Paymaster != Address.Zero)
            {
                if (!_stateProvider.AccountExists(userOperation.Paymaster) 
                    || !_stateProvider.IsContract(userOperation.Paymaster)
                    || _bannedPaymasters.Contains(userOperation.Paymaster))
                {
                    return ResultWrapper<Keccak>.Fail("paymaster is used but is not a contract or is banned");
                }
            }

            // make sure op not already in pool
            if (_userOperationSortedPool.GetSnapshot().Contains(userOperation))
            {
                return ResultWrapper<Keccak>.Fail("userOp is already present in the pool");
            }
            
            Task<ResultWrapper<Keccak>> successfulSimulationTask = Simulate(userOperation, _blockTree.Head!.Header);
            ResultWrapper<Keccak> successfulSimulation = successfulSimulationTask.Result;

            return successfulSimulation;
        }

        private async Task<ResultWrapper<Keccak>> Simulate(UserOperation userOperation, BlockHeader parent)
        {
            ResultWrapper<Keccak> success = await _userOperationSimulator.Simulate(
                userOperation, 
                parent, 
                CancellationToken.None, 
                _timestamper.UnixTime.Seconds);

            return success;
        }
        
    }
}
