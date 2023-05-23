// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Nethermind.Abi;
using Nethermind.AccountAbstraction.Data;
using Nethermind.Blockchain;
using Nethermind.Config;
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
        private readonly AbiDefinition _entryPointContractAbi;
        private readonly Address _entryPointContractAddress;
        private readonly Address[] _whitelistedPaymasters;
        private readonly ISpecProvider _specProvider;
        private readonly IUserOperationTxBuilder _userOperationTxBuilder;
        private readonly IReadOnlyStateProvider _stateProvider;
        private readonly ReadOnlyTxProcessingEnvFactory _readOnlyTxProcessingEnvFactory;
        private readonly ITimestamper _timestamper;
        private readonly IBlocksConfig _blocksConfig;
        private readonly IAbiEncoder _abiEncoder;
        private readonly ILogger _logger;

        public UserOperationSimulator(
            IUserOperationTxBuilder userOperationTxBuilder,
            IReadOnlyStateProvider stateProvider,
            ReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory,
            AbiDefinition entryPointContractAbi,
            Address entryPointContractAddress,
            Address[] whitelistedPaymasters,
            ISpecProvider specProvider,
            ITimestamper timestamper,
            ILogManager logManager,
            IBlocksConfig blocksConfig)
        {
            _userOperationTxBuilder = userOperationTxBuilder;
            _stateProvider = stateProvider;
            _readOnlyTxProcessingEnvFactory = readOnlyTxProcessingEnvFactory;
            _entryPointContractAbi = entryPointContractAbi;
            _entryPointContractAddress = entryPointContractAddress;
            _whitelistedPaymasters = whitelistedPaymasters;
            _specProvider = specProvider;
            _timestamper = timestamper;
            _blocksConfig = blocksConfig;
            _logger = logManager.GetClassLogger<UserOperationSimulator>();

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

            IEip1559Spec specFor1559 = _specProvider.GetSpecFor1559(parent.Number + 1);
            ReadOnlyTxProcessingEnv txProcessingEnv = _readOnlyTxProcessingEnvFactory.Create();
            ITransactionProcessor transactionProcessor = txProcessingEnv.Build(_stateProvider.StateRoot);

            // wrap userOp into a tx calling the simulateWallet function off-chain from zero-address (look at EntryPoint.sol for more context)
            Transaction simulateValidationTransaction =
                BuildSimulateValidationTransaction(userOperation, parent, specFor1559);

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
                userOperation.InitCode != Bytes.Empty, userOperation.Sender,
                userOperation.Paymaster,
                _entryPointContractAddress,
                _logger
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
            IEip1559Spec specfor1559)
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
                specfor1559,
                _stateProvider.GetNonce(Address.Zero),
                true);

            return transaction;
        }

        [Todo("Refactor once BlockchainBridge is separated")]
        public BlockchainBridge.CallOutput EstimateGas(BlockHeader header, Transaction tx, CancellationToken cancellationToken)
        {
            ReadOnlyTxProcessingEnv txProcessingEnv = _readOnlyTxProcessingEnvFactory.Create();
            using IReadOnlyTransactionProcessor transactionProcessor = txProcessingEnv.Build(header.StateRoot!);

            EstimateGasTracer estimateGasTracer = new();
            (bool Success, string Error) tryCallResult = TryCallAndRestore(
                transactionProcessor,
                header,
                Math.Max(header.Timestamp + 1, _timestamper.UnixTime.Seconds),
                tx,
                true,
                estimateGasTracer.WithCancellation(cancellationToken));

            GasEstimator gasEstimator = new(transactionProcessor, _stateProvider, _specProvider, _blocksConfig);
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
            ulong timestamp,
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
            ulong timestamp,
            Transaction transaction,
            bool treatBlockHeaderAsParentBlock,
            ITxTracer tracer)
        {
            transaction.SenderAddress ??= Address.SystemUser;

            if (transaction.Nonce == 0)
            {
                transaction.Nonce = _stateProvider.GetNonce(transaction.SenderAddress);
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
                ? BaseFeeCalculator.Calculate(blockHeader, _specProvider.GetSpec(callHeader))
                : blockHeader.BaseFeePerGas;

            transaction.Hash = transaction.CalculateHash();
            transactionProcessor.CallAndRestore(transaction, callHeader, tracer);
        }
    }
}
