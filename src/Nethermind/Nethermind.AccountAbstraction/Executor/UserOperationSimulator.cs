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
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Nethermind.Abi;
using Nethermind.AccountAbstraction.Data;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.AccountAbstraction.Executor
{
    public class UserOperationSimulator : IUserOperationSimulator
    {
        private readonly IBlockTree _blockTree;
        private readonly Address _create2FactoryAddress;
        private readonly IReadOnlyDbProvider _dbProvider;
        private readonly AbiDefinition _entryPointContractAbi;
        private readonly ILogManager _logManager;
        private readonly Address _entryPointContractAddress;
        private readonly Address[] _whitelistedPaymasters;
        private readonly ISpecProvider _specProvider;
        private readonly IUserOperationTxBuilder _userOperationTxBuilder;
        private readonly IStateProvider _stateProvider;
        private readonly IStateReader _stateReader;
        private readonly IReadOnlyTrieStore _trieStore;
        private readonly ITimestamper _timestamper;

        private readonly IAbiEncoder _abiEncoder;

        public UserOperationSimulator(
            IUserOperationTxBuilder userOperationTxBuilder,
            IStateProvider stateProvider,
            IStateReader stateReader,
            AbiDefinition entryPointContractAbi,
            Address create2FactoryAddress,
            Address entryPointContractAddress,
            Address[] whitelistedPaymasters,
            ISpecProvider specProvider,
            IBlockTree blockTree,
            IDbProvider dbProvider,
            IReadOnlyTrieStore trieStore,
            ITimestamper timestamper,
            ILogManager logManager)
        {
            _userOperationTxBuilder = userOperationTxBuilder;
            _stateProvider = stateProvider;
            _stateReader = stateReader;
            _entryPointContractAbi = entryPointContractAbi;
            _create2FactoryAddress = create2FactoryAddress;
            _entryPointContractAddress = entryPointContractAddress;
            _whitelistedPaymasters = whitelistedPaymasters;
            _specProvider = specProvider;
            _blockTree = blockTree;
            _dbProvider = dbProvider.AsReadOnly(false);
            _trieStore = trieStore;
            _timestamper = timestamper;
            _logManager = logManager;

            _abiEncoder = new AbiEncoder();
        }

        public ResultWrapper<Keccak> Simulate(UserOperation userOperation,
            BlockHeader parent,
            UInt256? timestamp = null,
            CancellationToken cancellationToken = default)
        {
            if (userOperation.AlreadySimulated)
            {
                // codehash of all accessed addresses should not change between calls
                foreach (KeyValuePair<Address, Keccak> kv in userOperation.AddressesToCodeHashes)
                {
                    (Address address, Keccak expectedCodeHash) = kv;
                    Keccak codeHash = _stateProvider.GetCodeHash(address);
                    if (codeHash != expectedCodeHash)
                    {
                        return ResultWrapper<Keccak>.Fail($"codehash of address {address} changed since initial simulation");
                    }
                }
            }
            
            IReleaseSpec currentSpec = _specProvider.GetSpec(parent.Number + 1);
            ReadOnlyTxProcessingEnv txProcessingEnv =
                new(_dbProvider, _trieStore, _blockTree, _specProvider, _logManager);
            ITransactionProcessor transactionProcessor = txProcessingEnv.Build(_stateProvider.StateRoot);

            // wrap userOp into a tx calling the simulateWallet function off-chain from zero-address (look at EntryPoint.sol for more context)
            Transaction simulateValidationTransaction =
                BuildSimulateValidationTransaction(userOperation, parent, currentSpec);
            
            UserOperationSimulationResult simulationResult = SimulateValidation(simulateValidationTransaction, userOperation, parent, transactionProcessor);

            if (!simulationResult.Success)
                return ResultWrapper<Keccak>.Fail(simulationResult.Error ?? "unknown simulation failure");

            if (userOperation.AlreadySimulated)
            {
                // if previously simulated we must make sure it doesn't access any more than it did on the first round
                if (!userOperation.AccessList.AccessListContains(simulationResult.AccessList.Data))
                    return ResultWrapper<Keccak>.Fail("access list exceeded");
            }
            else
            {
                userOperation.AccessList = simulationResult.AccessList;
                userOperation.AddressesToCodeHashes = simulationResult.AddressesToCodeHashes;
                userOperation.AlreadySimulated = true;
            }

            return ResultWrapper<Keccak>.Success(userOperation.RequestId!);
        }

        private UserOperationSimulationResult SimulateValidation(
            Transaction transaction, 
            UserOperation userOperation, 
            BlockHeader parent,
            ITransactionProcessor transactionProcessor)
        {
            bool paymasterWhitelisted = _whitelistedPaymasters.Contains(userOperation.Paymaster);
            UserOperationTxTracer txTracer = new(
                transaction,
                paymasterWhitelisted,
                userOperation.InitCode != Bytes.Empty,
                _stateProvider, userOperation.Sender,
                userOperation.Paymaster,
                _entryPointContractAddress,
                _logManager.GetClassLogger()
            );

            transactionProcessor.Trace(transaction, parent, txTracer);

            FailedOp? failedOp = _userOperationTxBuilder.DecodeEntryPointOutputError(txTracer.Output);

            string? error = null;

            if (!txTracer.Success)
            {
                if (failedOp is not null)
                    error = failedOp.ToString()!;
                else
                    error = txTracer.Error;
                return UserOperationSimulationResult.Failed(error);
            }

            UserOperationAccessList userOperationAccessList = new(txTracer.AccessedStorage);
            
            IDictionary<Address, Keccak> addressesToCodeHashes = new Dictionary<Address, Keccak>();
            foreach (Address accessedAddress in txTracer.AccessedAddresses)
            {
                addressesToCodeHashes[accessedAddress] = _stateProvider.GetCodeHash(accessedAddress);
            }

            return new UserOperationSimulationResult()
            {
                Success = true,
                AccessList = userOperationAccessList,
                AddressesToCodeHashes = addressesToCodeHashes,
                Error = error
            };
        }

        private Transaction BuildSimulateValidationTransaction(
            UserOperation userOperation,
            BlockHeader parent,
            IReleaseSpec spec)
        {
            AbiSignature abiSignature = _entryPointContractAbi.Functions["simulateValidation"].GetCallInfo().Signature;
            UserOperationAbi userOperationAbi = userOperation.Abi;

            byte[] computedCallData = _abiEncoder.Encode(
                AbiEncodingStyle.IncludeSignature,
                abiSignature,
                userOperationAbi);

            Transaction transaction = _userOperationTxBuilder.BuildTransaction((long)userOperation.PreVerificationGas + (long)userOperation.VerificationGas, 
                computedCallData,
                Address.Zero, 
                parent, 
                spec, 
                _stateProvider.GetNonce(Address.Zero),
                true);

            return transaction;
        }

        [Todo("Refactor once BlockchainBridge is separated")]
        public BlockchainBridge.CallOutput EstimateGas(BlockHeader header, Transaction tx, CancellationToken cancellationToken)
        {
            ReadOnlyTxProcessingEnv txProcessingEnv =
                new(_dbProvider, _trieStore, _blockTree, _specProvider, _logManager);
            using IReadOnlyTransactionProcessor transactionProcessor = txProcessingEnv.Build(header.StateRoot!);
            
            EstimateGasTracer estimateGasTracer = new();
            (bool Success, string Error) tryCallResult = TryCallAndRestore(
                transactionProcessor,
                header,
                UInt256.Max(header.Timestamp + 1, _timestamper.UnixTime.Seconds),
                tx,
                true,
                estimateGasTracer.WithCancellation(cancellationToken));

            GasEstimator gasEstimator = new(transactionProcessor, _stateProvider, _specProvider);
            long estimate = gasEstimator.Estimate(tx, header, estimateGasTracer);

            return new BlockchainBridge.CallOutput 
            {
                Error = tryCallResult.Success ? estimateGasTracer.Error : tryCallResult.Error, 
                GasSpent = estimate, 
                InputError = !tryCallResult.Success
            };
        }
        
        private (bool Success, string Error) TryCallAndRestore(
            ITransactionProcessor transactionProcessor,
            BlockHeader blockHeader,
            in UInt256 timestamp,
            Transaction transaction,
            bool treatBlockHeaderAsParentBlock,
            ITxTracer tracer)
        {
            try
            {
                CallAndRestore(transactionProcessor, blockHeader, timestamp, transaction, treatBlockHeaderAsParentBlock, tracer);
                return (true, string.Empty);
            }
            catch (InsufficientBalanceException ex)
            {
                return (false, ex.Message);
            }
        }

        private void CallAndRestore(
            ITransactionProcessor transactionProcessor,
            BlockHeader blockHeader,
            in UInt256 timestamp,
            Transaction transaction,
            bool treatBlockHeaderAsParentBlock,
            ITxTracer tracer)
        {
            if (transaction.SenderAddress == null)
            {
                transaction.SenderAddress = Address.SystemUser;
            }
            
            if (transaction.Nonce == 0)
            {
                transaction.Nonce = GetNonce(_stateProvider.StateRoot, transaction.SenderAddress);
            }

            BlockHeader callHeader = new(
                blockHeader.Hash!,
                Keccak.OfAnEmptySequenceRlp,
                Address.Zero,
                0,
                treatBlockHeaderAsParentBlock ? blockHeader.Number + 1 : blockHeader.Number,
                blockHeader.GasLimit,
                timestamp,
                Array.Empty<byte>());

            callHeader.BaseFeePerGas = treatBlockHeaderAsParentBlock
                ? BaseFeeCalculator.Calculate(blockHeader, _specProvider.GetSpec(callHeader.Number))
                : blockHeader.BaseFeePerGas;

            transaction.Hash = transaction.CalculateHash();
            transactionProcessor.CallAndRestore(transaction, callHeader, tracer);
        }
        
        private UInt256 GetNonce(Keccak stateRoot, Address address)
        {
            return _stateReader.GetNonce(stateRoot, address);
        }
    }
}
