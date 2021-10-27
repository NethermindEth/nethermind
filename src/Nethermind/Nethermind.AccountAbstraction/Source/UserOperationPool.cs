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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Executor;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.AccountAbstraction.Source
{
    public class UserOperationPool : IUserOperationPool
    {
        private readonly IAccountAbstractionConfig _accountAbstractionConfig;
        private readonly IBlockTree _blockTree;
        private readonly Address _entryPointAddress;
        private readonly ILogger _logger;
        private readonly IPaymasterThrottler _paymasterThrottler;
        private readonly IReceiptFinder _receiptFinder;
        private readonly ISigner _signer;
        private readonly IStateProvider _stateProvider;
        private readonly ITimestamper _timestamper;
        private readonly Keccak _userOperationEventTopic;
        private readonly IUserOperationSimulator _userOperationSimulator;
        private readonly UserOperationSortedPool _userOperationSortedPool;

        private readonly Dictionary<long, List<UserOperation>> _userOperationsToDelete = new();

        public UserOperationPool(
            IAccountAbstractionConfig accountAbstractionConfig,
            IBlockTree blockTree,
            Address entryPointAddress,
            ILogger logger,
            IPaymasterThrottler paymasterThrottler,
            IReceiptFinder receiptFinder,
            ISigner signer,
            IStateProvider stateProvider,
            ITimestamper timestamper,
            IUserOperationSimulator userOperationSimulator,
            UserOperationSortedPool userOperationSortedPool)
        {
            _blockTree = blockTree;
            _stateProvider = stateProvider;
            _paymasterThrottler = paymasterThrottler;
            _receiptFinder = receiptFinder;
            _signer = signer;
            _timestamper = timestamper;
            _entryPointAddress = entryPointAddress;
            _logger = logger;
            _accountAbstractionConfig = accountAbstractionConfig;
            _userOperationSortedPool = userOperationSortedPool;
            _userOperationSimulator = userOperationSimulator;

            _userOperationEventTopic = new Keccak("0xc27a60e61c14607957b41fa2dad696de47b2d80e390d0eaaf1514c0cd2034293");

            _blockTree.NewHeadBlock += NewHead;
        }

        public IEnumerable<UserOperation> GetUserOperations()
        {
            return _userOperationSortedPool.GetSnapshot();
        }

        public ResultWrapper<Keccak> AddUserOperation(UserOperation userOperation)
        {
            Metrics.UserOperationsReceived++;
            if (_logger.IsDebug) _logger.Debug($"UserOperation {userOperation.Hash} received");
            ResultWrapper<Keccak> result = ValidateUserOperation(userOperation);
            if (result.Result == Result.Success)
            {
                if (_logger.IsDebug) _logger.Debug($"UserOperation {userOperation.Hash} validation succeeded");
                if (_userOperationSortedPool.TryInsert(userOperation, userOperation))
                {
                    Metrics.UserOperationsPending++;
                    _paymasterThrottler.IncrementOpsSeen(userOperation.Paymaster);
                    if (_logger.IsDebug) _logger.Debug($"UserOperation {userOperation.Hash} inserted into pool");
                    return ResultWrapper<Keccak>.Success(userOperation.Hash);
                }

                if (_logger.IsDebug) _logger.Debug($"UserOperation {userOperation.Hash} failed to be inserted into pool");
                return ResultWrapper<Keccak>.Fail("failed to insert userOp into pool");
            }

            if (_logger.IsDebug) _logger.Debug($"UserOperation {userOperation.Hash} validation failed because: {result.Result.Error}");
            return result;
        }

        public bool RemoveUserOperation(UserOperation userOperation)
        {
            return _userOperationSortedPool.TryRemove(userOperation);
        }

        private void NewHead(object? sender, BlockEventArgs e)
        {
            // remove any user operations that were only allowed to stay for 10 blocks due to throttled paymasters
            Block block = e.Block;
            if (_userOperationsToDelete.ContainsKey(block.Number))
            {
                foreach (var userOperation in _userOperationsToDelete[block.Number]) RemoveUserOperation(userOperation);
            }

            // find any userOps included on chain submitted by this miner, delete from the pool
            TxReceipt[] receipts = _receiptFinder.Get(block);
            TxReceipt[] entryPointReceipts = receipts
                .Where(r => r.Recipient is not null && r.Recipient == _entryPointAddress).ToArray();

            LogEntry[] logs = entryPointReceipts
                .Where(r => r.Sender is not null && r.Sender == _signer.Address)
                .SelectMany(r => r.Logs ?? Array.Empty<LogEntry>())
                .Where(l => l.Topics[0] == _userOperationEventTopic)
                .ToArray();

            foreach (var log in logs)
            {
                Address senderAddress = new(log.Topics[1]);
                Address paymasterAddress = new(log.Topics[2]);
                UInt256 nonce = new(log.Data.Slice(0, 32), true);
                IDictionary<Address, UserOperation[]> bucketSnapshot = _userOperationSortedPool.GetBucketSnapshot();
                if (bucketSnapshot.ContainsKey(senderAddress))
                {
                    UserOperation[] userOperationsWithSender = bucketSnapshot[senderAddress];
                    IEnumerable<UserOperation> userOperationsToRemove =
                        userOperationsWithSender.Where(op => op.Nonce == nonce && op.Paymaster == paymasterAddress);
                    foreach (var userOperation in userOperationsToRemove)
                    {
                        if (_logger.IsDebug) _logger.Debug($"UserOperation {userOperation.Hash} removed from pool after being included by miner");
                        Metrics.UserOperationsIncluded++;
                        _paymasterThrottler.IncrementOpsIncluded(paymasterAddress);
                        RemoveUserOperation(userOperation);
                    }
                }
            }
        }

        private ResultWrapper<Keccak> ValidateUserOperation(UserOperation userOperation)
        {
            PaymasterStatus paymasterStatus =
                _paymasterThrottler.GetPaymasterStatus(userOperation.Paymaster);

            switch (paymasterStatus)
            {
                case PaymasterStatus.Ok: break;
                case PaymasterStatus.Banned: return ResultWrapper<Keccak>.Fail("paymaster banned");
                case PaymasterStatus.Throttled:
                {
                    IEnumerable<UserOperation> poolUserOperations = GetUserOperations();
                    if (poolUserOperations.Any(poolOp => poolOp.Paymaster == userOperation.Paymaster))
                        return ResultWrapper<Keccak>.Fail(
                            $"paymaster throttled and userOp with paymaster {userOperation.Paymaster} is already present in the pool");
                    break;
                }
            }

            if (userOperation.MaxFeePerGas < _accountAbstractionConfig.MinimumGasPrice)
                return ResultWrapper<Keccak>.Fail("maxFeePerGas below minimum gas price");

            if (userOperation.CallGas < Transaction.BaseTxGasCost)
                return ResultWrapper<Keccak>.Fail($"callGas too low, must be at least {Transaction.BaseTxGasCost}");

            // make sure target account exists
            if (
                userOperation.Sender == Address.Zero
                || !(_stateProvider.AccountExists(userOperation.Sender) || userOperation.InitCode != Bytes.Empty))
                return ResultWrapper<Keccak>.Fail("sender doesn't exist");

            // make sure paymaster is a contract (if paymaster is used) and is not on banned list
            if (userOperation.Paymaster != Address.Zero)
            {
                if (!_stateProvider.AccountExists(userOperation.Paymaster)
                    || !_stateProvider.IsContract(userOperation.Paymaster))
                    return ResultWrapper<Keccak>.Fail("paymaster is used but is not a contract or is banned");
            }

            // make sure op not already in pool
            if (_userOperationSortedPool.GetSnapshot().Contains(userOperation))
                return ResultWrapper<Keccak>.Fail("userOp is already present in the pool");

            Task<ResultWrapper<Keccak>> successfulSimulationTask = Simulate(userOperation, _blockTree.Head!.Header);
            ResultWrapper<Keccak> successfulSimulation = successfulSimulationTask.Result;

            // throttled userOp can only stay for 10 blocks
            if (paymasterStatus == PaymasterStatus.Throttled && successfulSimulation.Result == Result.Success)
            {
                long blockNumberToDelete = _blockTree.Head!.Number + 10;
                if (_userOperationsToDelete.ContainsKey(blockNumberToDelete))
                    _userOperationsToDelete[blockNumberToDelete].Add(userOperation);
                else
                    _userOperationsToDelete.Add(blockNumberToDelete, new List<UserOperation> {userOperation});
            }

            return successfulSimulation;
        }

        private async Task<ResultWrapper<Keccak>> Simulate(UserOperation userOperation, BlockHeader parent)
        {
            Metrics.UserOperationsSimulated++;
            ResultWrapper<Keccak> success = await _userOperationSimulator.Simulate(
                userOperation,
                parent,
                CancellationToken.None,
                _timestamper.UnixTime.Seconds);

            return success;
        }
    }
}
