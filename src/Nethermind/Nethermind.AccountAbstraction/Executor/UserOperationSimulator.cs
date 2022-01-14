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

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.AccountAbstraction.Data;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
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
        private readonly IAccountAbstractionConfig _config;
        private readonly Address _create2FactoryAddress;
        private readonly IReadOnlyDbProvider _dbProvider;
        private readonly AbiDefinition _entryPointContractAbi;
        private readonly ILogManager _logManager;
        private readonly IBlockPreprocessorStep _recoveryStep;
        private readonly ISigner _signer;
        private readonly Address _entryPointContractAddress;
        private readonly ISpecProvider _specProvider;
        private readonly IStateProvider _stateProvider;
        private readonly IReadOnlyTrieStore _trieStore;

        private readonly IAbiEncoder _abiEncoder;

        public UserOperationSimulator(
            IStateProvider stateProvider,
            AbiDefinition entryPointContractAbi,
            ISigner signer,
            IAccountAbstractionConfig config,
            Address create2FactoryAddress,
            Address entryPointContractAddress,
            ISpecProvider specProvider,
            IBlockTree blockTree,
            IDbProvider dbProvider,
            IReadOnlyTrieStore trieStore,
            ILogManager logManager,
            IBlockPreprocessorStep recoveryStep)
        {
            _stateProvider = stateProvider;
            _entryPointContractAbi = entryPointContractAbi;
            _signer = signer;
            _config = config;
            _create2FactoryAddress = create2FactoryAddress;
            _entryPointContractAddress = entryPointContractAddress;
            _specProvider = specProvider;
            _blockTree = blockTree;
            _dbProvider = dbProvider.AsReadOnly(false);
            _trieStore = trieStore;
            _logManager = logManager;
            _recoveryStep = recoveryStep;

            _abiEncoder = new AbiEncoder();
        }

        public Transaction BuildTransactionFromUserOperations(IEnumerable<UserOperation> userOperations,
            BlockHeader parent, IReleaseSpec spec)
        {
            byte[] computedCallData;
            long gasLimit;

            // use handleOp is only one op is used, handleOps if multiple
            UserOperation[] userOperationArray = userOperations.ToArray();
            if (userOperationArray.Length == 1)
            {
                UserOperation userOperation = userOperationArray[0];

                AbiSignature abiSignature = _entryPointContractAbi.Functions["handleOp"].GetCallInfo().Signature;
                computedCallData = _abiEncoder.Encode(
                    AbiEncodingStyle.IncludeSignature,
                    abiSignature,
                    userOperation.Abi, _signer.Address);

                gasLimit = (long)userOperation.VerificationGas + (long)userOperation.CallGas +
                           100000; // TODO WHAT CONSTANT
            }
            else
            {
                AbiSignature abiSignature = _entryPointContractAbi.Functions["handleOps"].GetCallInfo().Signature;
                computedCallData = _abiEncoder.Encode(
                    AbiEncodingStyle.IncludeSignature,
                    abiSignature,
                    userOperationArray.Select(op => op.Abi).ToArray(), _signer.Address);

                gasLimit = userOperationArray.Aggregate((long)0,
                    (sum, operation) =>
                        sum + (long)operation.VerificationGas + (long)operation.CallGas + 100000); // TODO WHAT CONSTANT
            }

            Transaction transaction =
                BuildTransaction(gasLimit, computedCallData, _signer.Address, parent, spec, false);

            return transaction;
        }

        public Task<ResultWrapper<Keccak>> Simulate(UserOperation userOperation,
            BlockHeader parent,
            UInt256? timestamp = null,
            CancellationToken cancellationToken = default)
        {
            IReleaseSpec currentSpec = _specProvider.GetSpec(parent.Number + 1);
            ReadOnlyTxProcessingEnv txProcessingEnv =
                new(_dbProvider, _trieStore, _blockTree, _specProvider, _logManager);
            ITransactionProcessor transactionProcessor = txProcessingEnv.Build(_stateProvider.StateRoot);

            // wrap userOp into a tx calling the simulateWallet function off-chain from zero-address (look at EntryPoint.sol for more context)
            Transaction simulateValidationTransaction =
                BuildSimulateValidationTransaction(userOperation, parent, currentSpec);
            (bool validationSuccess, UserOperationAccessList validationAccessList, string? error) =
                SimulateValidation(simulateValidationTransaction, userOperation, parent, transactionProcessor);

            if (!validationSuccess)
                return Task.FromResult(ResultWrapper<Keccak>.Fail(error ?? "unknown simulation failure"));

            if (userOperation.AlreadySimulated)
            {
                // if previously simulated we must make sure it doesn't access any more than it did on the first round
                if (!userOperation.AccessList.AccessListContains(validationAccessList.Data))
                    return Task.FromResult(ResultWrapper<Keccak>.Fail("access list exceeded"));
            }
            else
            {
                userOperation.AccessList = validationAccessList;
                userOperation.AlreadySimulated = true;
            }

            return Task.FromResult(ResultWrapper<Keccak>.Success(userOperation.Hash));
        }

        private (bool success, UserOperationAccessList accessList, string? error) SimulateValidation(
            Transaction transaction, UserOperation userOperation, BlockHeader parent,
            ITransactionProcessor transactionProcessor)
        {
            UserOperationBlockTracer blockTracer = CreateBlockTracer(parent, userOperation);
            ITxTracer txTracer = blockTracer.StartNewTxTrace(transaction);
            transactionProcessor.CallAndRestore(transaction, parent, txTracer);
            blockTracer.EndTxTrace();

            string? error = null;

            if (!blockTracer.Success)
            {
                if (blockTracer.FailedOp is not null)
                    error = blockTracer.FailedOp.ToString()!;
                else
                    error = blockTracer.Error;
                return (false, UserOperationAccessList.Empty, error);
            }

            UserOperationAccessList userOperationAccessList = new(blockTracer.AccessedStorage);

            return (true, userOperationAccessList, error);
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

            Transaction transaction = BuildTransaction((long)userOperation.VerificationGas, computedCallData,
                Address.Zero, parent, spec, true);

            return transaction;
        }

        private Transaction BuildTransaction(long gaslimit, byte[] callData, Address sender, BlockHeader parent,
            IReleaseSpec spec, bool systemTransaction)
        {
            Transaction transaction = systemTransaction ? new SystemTransaction() : new Transaction();

            UInt256 fee = BaseFeeCalculator.Calculate(parent, spec);

            transaction.GasPrice = fee;
            transaction.GasLimit = gaslimit;
            transaction.To = _entryPointContractAddress;
            transaction.ChainId = _specProvider.ChainId;
            transaction.Nonce = _stateProvider.GetNonce(_signer.Address);
            transaction.Value = 0;
            transaction.Data = callData;
            transaction.Type = TxType.EIP1559;
            transaction.DecodedMaxFeePerGas = fee;
            transaction.SenderAddress = sender;

            if (!systemTransaction) _signer.Sign(transaction);
            transaction.Hash = transaction.CalculateHash();

            return transaction;
        }

        private UserOperationBlockTracer CreateBlockTracer(BlockHeader parent, UserOperation userOperation)
        {
            return new(parent.GasLimit, userOperation, _stateProvider, _entryPointContractAbi, _create2FactoryAddress,
                _entryPointContractAddress, _logManager.GetClassLogger());
        }
    }
}
