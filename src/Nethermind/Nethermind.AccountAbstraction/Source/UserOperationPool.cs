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
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.AccountAbstraction.Broadcaster;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Executor;
using Nethermind.AccountAbstraction.Network;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Blockchain.Find;
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
    public class UserOperationPool : IUserOperationPool, IDisposable
    {
        private readonly IAccountAbstractionConfig _accountAbstractionConfig;
        private readonly IBlockTree _blockTree;
        private readonly Address _entryPointAddress;
        private readonly ILogger _logger;
        private readonly IPaymasterThrottler _paymasterThrottler;
        private readonly ILogFinder _logFinder;
        private readonly ISigner _signer;
        private readonly IStateProvider _stateProvider;
        private readonly ITimestamper _timestamper;
        private readonly Keccak _userOperationEventTopic;
        private readonly IUserOperationSimulator _userOperationSimulator;
        private readonly UserOperationSortedPool _userOperationSortedPool;

        private readonly ConcurrentDictionary<long, HashSet<Keccak>> _userOperationsToDelete = new();
        private readonly ConcurrentDictionary<long, HashSet<UserOperation>> _removedUserOperations = new();
        private readonly UserOperationBroadcaster _broadcaster;

        private readonly Channel<BlockReplacementEventArgs> _headBlocksChannel = Channel.CreateUnbounded<BlockReplacementEventArgs>(new UnboundedChannelOptions() { SingleReader = true, SingleWriter = true });
        private readonly ulong _chainId;
        public UserOperationPool(
            IAccountAbstractionConfig accountAbstractionConfig,
            IBlockTree blockTree,
            Address entryPointAddress,
            ILogger logger,
            IPaymasterThrottler paymasterThrottler,
            ILogFinder logFinder,
            ISigner signer,
            IStateProvider stateProvider,
            ITimestamper timestamper,
            IUserOperationSimulator userOperationSimulator,
            UserOperationSortedPool userOperationSortedPool,
            ulong chainId
            )
        {
            _blockTree = blockTree;
            _stateProvider = stateProvider;
            _paymasterThrottler = paymasterThrottler;
            _logFinder = logFinder;
            _signer = signer;
            _timestamper = timestamper;
            _entryPointAddress = entryPointAddress;
            _logger = logger;
            _accountAbstractionConfig = accountAbstractionConfig;
            _userOperationSortedPool = userOperationSortedPool;
            _userOperationSimulator = userOperationSimulator;
            _chainId = chainId;

            // topic hash emitted by a successful user operation
            _userOperationEventTopic = new Keccak("0x33fd4d1f25a5461bea901784a6571de6debc16cd0831932c22c6969cd73ba994");

            MemoryAllowance.MemPoolSize = accountAbstractionConfig.UserOperationPoolSize;

            _broadcaster = new UserOperationBroadcaster(logger);

            _blockTree.BlockAddedToMain += OnBlockAdded;

            ProcessNewBlocks();
        }

        private void OnBlockAdded(object? sender, BlockReplacementEventArgs e)
        {
            try
            {
                _headBlocksChannel.Writer.TryWrite(e);
            }
            catch (Exception exception)
            {
                if (_logger.IsError)
                    _logger.Error(
                        $"Couldn't correctly add or remove user operations from UserOperationPool after processing block {e.Block!.ToString(Block.Format.FullHashAndNumber)}.", exception);
            }
        }

        private void ProcessNewBlocks()
        {
            Task.Factory.StartNew(async () =>
            {
                while (await _headBlocksChannel.Reader.WaitToReadAsync())
                {
                    while (_headBlocksChannel.Reader.TryRead(out BlockReplacementEventArgs? args))
                    {
                        try
                        {
                            ReAddReorganizedUserOperations(args.PreviousBlock);
                            RemoveProcessedUserOperations(args.Block);
                        }
                        catch (Exception e)
                        {
                            if (_logger.IsDebug) _logger.Debug($"UserOperationPool failed to update after block {args.Block.ToString(Block.Format.FullHashAndNumber)} with exception {e}");
                        }
                    }
                }
            }, TaskCreationOptions.LongRunning).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (_logger.IsError) _logger.Error($"UserOperationPool update after block queue failed.", t.Exception);
                }
            });
        }

        private void ReAddReorganizedUserOperations(Block? previousBlock)
        {
            if (previousBlock is not null && _removedUserOperations.ContainsKey(previousBlock.Number))
            {
                foreach (UserOperation op in _removedUserOperations[previousBlock.Number])
                {
                    AddUserOperation(op);
                }
            }
        }

        public IEnumerable<UserOperation> GetUserOperations()
        {
            return _userOperationSortedPool.GetSnapshot();
        }

        public bool IncludesUserOperationWithSenderAndNonce(Address sender, UInt256 nonce)
        {
            if (_userOperationSortedPool.TryGetBucket(sender, out UserOperation[] userOperations))
            {
                return userOperations.Any(op => op.Nonce == nonce);
            }
            else
            {
                return false;
            }
        }

        public bool CanInsert(UserOperation userOperation)
        {
            return _userOperationSortedPool.CanInsert(userOperation);
        }

        public ResultWrapper<Keccak> AddUserOperation(UserOperation userOperation)
        {
            Metrics.UserOperationsReceived++;
            if (_logger.IsDebug) _logger.Debug($"UserOperation {userOperation.Hash} received");

            UserOperationEventArgs userOperationEventArgs = new(userOperation);
            NewReceived?.Invoke(this, userOperationEventArgs);
            
            ResultWrapper<Keccak> result = ValidateUserOperation(userOperation);
            if (result.Result == Result.Success)
            {
                if (_logger.IsDebug) _logger.Debug($"UserOperation {userOperation.Hash} validation succeeded");
                if (_userOperationSortedPool.TryInsert(userOperation.Hash, userOperation))
                {
                    Metrics.UserOperationsPending++;
                    _paymasterThrottler.IncrementOpsSeen(userOperation.Paymaster);
                    if (_logger.IsDebug) _logger.Debug($"UserOperation {userOperation.Hash} inserted into pool");
                    _broadcaster.BroadcastOnce(new UserOperationWithEntryPoint(userOperation, _entryPointAddress));                    
                    NewPending?.Invoke(this, userOperationEventArgs);
                    
                    return ResultWrapper<Keccak>.Success(userOperation.CalculateRequestId(_entryPointAddress, _chainId));
                }

                if (_logger.IsDebug) _logger.Debug($"UserOperation {userOperation.Hash} failed to be inserted into pool");
                return ResultWrapper<Keccak>.Fail("failed to insert userOp into pool");
            }

            if (_logger.IsDebug) _logger.Debug($"UserOperation {userOperation.Hash} validation failed because: {result.Result.Error}");

            return result;
        }

        public bool RemoveUserOperation(Keccak? userOperationHash)
        {
            return userOperationHash is not null && _userOperationSortedPool.TryRemove(userOperationHash);
        }

        private void RemoveProcessedUserOperations(Block block)
        {
            // clean storage of user operations included in past blocks beyond supported number of reorganized blocks
            _removedUserOperations.TryRemove(block.Number - Reorganization.MaxDepth, out _);

            // remove any user operations that were only allowed to stay for 10 blocks due to throttled paymasters
            if (_userOperationsToDelete.ContainsKey(block.Number))
            {
                foreach (var userOperationHash in _userOperationsToDelete[block.Number]) RemoveUserOperation(userOperationHash);
            }

            BlockParameter currentBlockParameter = new BlockParameter(block.Number);
            AddressFilter entryPointAddressFilter = new AddressFilter(_entryPointAddress);
            IEnumerable<FilterLog> foundLogs = _logFinder.FindLogs(new LogFilter(0,
                currentBlockParameter,
                currentBlockParameter,
                entryPointAddressFilter,
                new SequenceTopicsFilter(new TopicExpression[] { new SpecificTopic(_userOperationEventTopic) })));

            // find any userOps included on chain submitted by this miner, delete from the pool
            foreach (FilterLog log in foundLogs)
            {
                if (log?.Topics[0] == _userOperationEventTopic)
                {
                    Address senderAddress = new(log.Topics[2]);
                    Address paymasterAddress = new(log.Topics[3]);
                    UInt256 nonce = new(log.Data.Slice(0, 32), true);
                    if (_userOperationSortedPool.TryGetBucket(senderAddress, out UserOperation[] opsOfSender))
                    {
                        foreach (UserOperation op in opsOfSender)
                        {
                            if (op.Nonce == nonce && op.Paymaster == paymasterAddress)
                            {
                                if (_logger.IsDebug) _logger.Debug($"UserOperation {op.Hash} removed from pool after being included by miner");
                                Metrics.UserOperationsIncluded++;
                                _paymasterThrottler.IncrementOpsIncluded(paymasterAddress);
                                RemoveUserOperation(op.Hash);
                                _removedUserOperations.AddOrUpdate(block.Number,
                                    k => new HashSet<UserOperation>() { op },
                                    (k, v) =>
                                    {
                                        v.Add(op);
                                        return v;
                                    });
                            }
                        }
                    }
                }
            }
        }

        private ResultWrapper<Keccak> ValidateUserOperation(UserOperation userOperation)
        {
            // make sure op not already in pool
            if (_userOperationSortedPool.TryGetValue(userOperation.Hash, out _))
                return ResultWrapper<Keccak>.Fail("userOp is already present in the pool");

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

            if (_userOperationSortedPool.UserOperationWouldOverflowSenderBucket(userOperation))
            {
                return ResultWrapper<Keccak>.Fail($"the pool already contains the maximum {_accountAbstractionConfig.MaximumUserOperationPerSender} user operations from the {userOperation.Sender} sender");
            }

            if (userOperation.MaxFeePerGas < _accountAbstractionConfig.MinimumGasPrice)
                return ResultWrapper<Keccak>.Fail($"maxFeePerGas below minimum gas price {_accountAbstractionConfig.MinimumGasPrice} wei");

            if (userOperation.CallGas < Transaction.BaseTxGasCost)
                return ResultWrapper<Keccak>.Fail($"callGas too low, must be at least {Transaction.BaseTxGasCost}");

            // make sure target account exists or is going to be created
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

            ResultWrapper<Keccak> successfulSimulation = Simulate(userOperation, _blockTree.Head!.Header);

            // throttled userOp can only stay for 10 blocks
            if (paymasterStatus == PaymasterStatus.Throttled && successfulSimulation.Result == Result.Success)
            {
                long blockNumberToDelete = _blockTree.Head!.Number + 10;
                _userOperationsToDelete.AddOrUpdate(blockNumberToDelete,
                    k => new HashSet<Keccak>() { userOperation.Hash },
                    (k, v) =>
                    {
                        v.Add(userOperation.Hash);
                        return v;
                    });
            }

            return successfulSimulation;
        }

        private ResultWrapper<Keccak> Simulate(UserOperation userOperation, BlockHeader parent)
        {
            Metrics.UserOperationsSimulated++;
            ResultWrapper<Keccak> success = _userOperationSimulator.Simulate(
                userOperation,
                parent,
                _timestamper.UnixTime.Seconds, CancellationToken.None);

            return success;
        }

        public void Dispose()
        {
            _blockTree.BlockAddedToMain -= OnBlockAdded;
            _headBlocksChannel.Writer.Complete();
        }

        public event EventHandler<UserOperationEventArgs>? NewReceived;
        public event EventHandler<UserOperationEventArgs>? NewPending;

    }
}
