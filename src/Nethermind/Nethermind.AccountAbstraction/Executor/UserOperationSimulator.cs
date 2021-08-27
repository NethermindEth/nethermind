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
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Source;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Contracts.Json;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Newtonsoft.Json;

namespace Nethermind.AccountAbstraction.Executor
{
    public class UserOperationSimulator : IUserOperationSimulator
    {
        private readonly IStateProvider _stateProvider;
        private readonly ISigner _signer;
        private readonly IAccountAbstractionConfig _config;
        private readonly ISpecProvider _specProvider;
        private readonly IBlockTree _blockTree;
        private readonly IReadOnlyDbProvider _dbProvider;
        private readonly IReadOnlyTrieStore _trieStore;
        private readonly ILogManager _logManager;
        private readonly IBlockPreprocessorStep _recoveryStep;

        private AbiDefinition _contract;

        public UserOperationSimulator(
            IStateProvider stateProvider,
            ISigner signer,
            IAccountAbstractionConfig config,
            ISpecProvider specProvider,
            IBlockTree blockTree,
            IDbProvider dbProvider,
            IReadOnlyTrieStore trieStore,
            ILogManager logManager,
            IBlockPreprocessorStep recoveryStep)
        {
            _stateProvider = stateProvider;
            _signer = signer;
            _config = config;
            _specProvider = specProvider;
            _blockTree = blockTree;
            _dbProvider = dbProvider.AsReadOnly(false);
            _trieStore = trieStore;
            _logManager = logManager;
            _recoveryStep = recoveryStep;
            
            using (StreamReader r = new StreamReader("Contracts/Singleton.json"))
            {
                string json = r.ReadToEnd();
                dynamic obj = JsonConvert.DeserializeObject(json)!;

                _contract = LoadContract(obj);
            }
        }

        private AbiDefinition LoadContract(dynamic obj)
        {
            AbiDefinitionParser parser = new();
            parser.RegisterAbiTypeFactory(new AbiTuple<UserOperationAbi>());
            AbiDefinition contract = parser.Parse(obj["abi"].ToString());
            AbiTuple<UserOperationAbi> userOperationAbi = new();
            return contract;
        }

        public Task<bool> Simulate(
            UserOperation userOperation, 
            BlockHeader parent,
            CancellationToken cancellationToken = default, 
            UInt256? timestamp = null)
        {
            Transaction userOperationTransaction = BuildSimulateTransactionFromUserOperations(userOperation, parent);
            
            ReadOnlyTxProcessingEnv txProcessingEnv = new(_dbProvider, _trieStore, _blockTree, _specProvider, _logManager);
            ITransactionProcessor transactionProcessor = txProcessingEnv.Build(_stateProvider.StateRoot);
            
            UserOperationBlockTracer blockTracer = CreateBlockTracer(userOperationTransaction, parent);
            ITxTracer txTracer = blockTracer.StartNewTxTrace(userOperationTransaction);
            transactionProcessor.CallAndRestore(userOperationTransaction, parent, txTracer);
            blockTracer.EndTxTrace();
            
            // reset
            userOperation.AccessListTouched = false;
            
            return Task.FromResult(blockTracer.Success);
        }

        public Transaction BuildSimulateTransactionFromUserOperations(
            UserOperation userOperation, 
            BlockHeader parent)
        {
            Address.TryParse(_config.SingletonContractAddress, out Address singletonContractAddress);
            IReleaseSpec currentSpec = _specProvider.GetSpec(parent.Number + 1);

            IAbiEncoder abiEncoder = new AbiEncoder();

            AbiSignature abiSignature = _contract.Functions["simulateWalletValidation"].GetCallInfo().Signature;
            UserOperationAbi userOperationAbi = userOperation.Abi;
            
            byte[] computedCallData = abiEncoder.Encode(
                AbiEncodingStyle.IncludeSignature,
                abiSignature,
                userOperationAbi);

            SystemTransaction transaction = new()
            {
                GasPrice = 0, // the bundler should in real scenarios be the miner
                GasLimit = (long) userOperation.VerificationGas + (long) userOperation.CallGas,
                To = singletonContractAddress,
                ChainId = _specProvider.ChainId,
                Nonce = _stateProvider.GetNonce(_signer.Address),
                Value = 0,
                Data = computedCallData
            };

            object test = abiEncoder.Decode(AbiEncodingStyle.IncludeSignature, abiSignature,
                Bytes.FromHexString(
                    "0xec7d10a800000000000000000000000000000000000000000000000000000000000000200000000000000000000000004ed7c70f96b99c776995fb64377f0d4ab3b0e1c10000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000018000000000000000000000000000000000000000000000000000000000000001a00000000000000000000000000000000000000000000000000000000000012917000000000000000000000000000000000000000000000000000000000007a120000000000000000000000000000000000000000000000000000000003d87cb2d000000000000000000000000000000000000000000000000000000003b9aca00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001c0000000000000000000000000fcffc8f5e5842888a2e58862123b89c0be6aa15d00000000000000000000000000000000000000000000000000000000000001e000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000413faf07e812b7adf2e76d2cb7118e0521e3b9c195ac238f0535e9a651cde89d05044b475b00be12ed435215077db246c28d2c8e2d4d0182296c6958b68b65298a1c00000000000000000000000000000000000000000000000000000000000000"));

            
            if (currentSpec.IsEip1559Enabled)
            {
                transaction.Type = TxType.EIP1559;
                transaction.DecodedMaxFeePerGas = BaseFeeCalculator.Calculate(parent, currentSpec);
            }
            else
            {
                transaction.Type = TxType.Legacy;
            }

            transaction.SenderAddress = Address.Zero;
            transaction.Hash = transaction.CalculateHash();

            //_stateProvider.CreateAccount(Address.Zero, 0);
            //_stateProvider.AddToBalance(Address.Zero, 1_000_000_000, currentSpec);

            return transaction;
        }

        private UserOperationBlockTracer CreateBlockTracer(Transaction userOperationTransaction, BlockHeader parent) =>
            new(parent.GasLimit, _signer.Address);
    }
}
