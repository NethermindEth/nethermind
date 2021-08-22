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
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Source;
using Nethermind.Blockchain;
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
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;

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
        }

        public Task<bool> Simulate(
            UserOperation userOperation, 
            BlockHeader parent,
            CancellationToken cancellationToken = default, 
            UInt256? timestamp = null)
        {
            Transaction userOperationTransaction = BuildTransactionFromUserOperations(new List<UserOperation>{userOperation}, parent);
            Block block = BuildBlock(userOperationTransaction, parent, timestamp);
            UserOperationBlockTracer blockTracer = CreateBlockTracer(userOperationTransaction, parent);
            ITracer tracer = CreateTracer();
            tracer.Trace(block, blockTracer.WithCancellation(cancellationToken));

            // reset
            userOperation.AccessListTouched = false;
            
            return Task.FromResult(blockTracer.Success);
        }

        public Transaction BuildTransactionFromUserOperations(
            IList<UserOperation> userOperations, 
            BlockHeader parent)
        {
            Address.TryParse(_config.SingletonContractAddress, out Address singletonContractAddress);
            IReleaseSpec currentSpec = _specProvider.GetSpec(parent.Number + 1);

            IReadOnlyDictionary<string, AbiType> userOperationRlp = new Dictionary<string, AbiType>
            {
                {"target", new AbiBytes(20)},
                {"callGas", new AbiUInt(64)},
                {"postCallGas", new AbiUInt(64)},
                {"gasPrice", AbiType.UInt256},
                {"callData", AbiType.DynamicBytes},
                {"signature", AbiType.DynamicBytes}
            };
            AbiSignature abiSignature = new AbiSignature("handleOps",
                new AbiArray(new AbiTuple(userOperationRlp)));

            IAbiEncoder abiEncoder = new AbiEncoder();
            byte[] computedCallData = abiEncoder.Encode(
                AbiEncodingStyle.None,
                abiSignature,
                userOperations);

            Transaction transaction = new()
            {
                GasPrice = 0, // the bundler should in real scenarios be the miner
                GasLimit = userOperations.Aggregate((long)0, (sum, op) => sum + op.CallGas),
                To = singletonContractAddress,
                ChainId = _specProvider.ChainId,
                Nonce = _stateProvider.GetNonce(_signer.Address),
                Value = 0,
                Data = computedCallData
            };
            if (currentSpec.IsEip1559Enabled)
            {
                transaction.Type = TxType.EIP1559;
                transaction.DecodedMaxFeePerGas = BaseFeeCalculator.Calculate(parent, currentSpec);
            }
            else
            {
                transaction.Type = TxType.Legacy;
            }

            _signer.Sign(transaction);
            transaction.Hash = transaction.CalculateHash();

            return transaction;
        }

        private UserOperationBlockTracer CreateBlockTracer(Transaction userOperationTransaction, BlockHeader parent) =>
            new(parent.GasLimit, _signer.Address);

        private ITracer CreateTracer()
        {
            ReadOnlyTxProcessingEnv txProcessingEnv = new(
                _dbProvider, _trieStore, _blockTree, _specProvider, _logManager);

            ReadOnlyChainProcessingEnv chainProcessingEnv = new(
                txProcessingEnv, Always.Valid, _recoveryStep, NoBlockRewards.Instance, new InMemoryReceiptStorage(),
                _dbProvider, _specProvider, _logManager);

            return new Tracer(txProcessingEnv.StateProvider, chainProcessingEnv.ChainProcessor,
                ProcessingOptions.ProducingBlock | ProcessingOptions.IgnoreParentNotOnMainChain);
        }

        private Block BuildBlock(Transaction transaction, BlockHeader parent, UInt256? timestamp)
        {
            BlockHeader header = new(
                parent.Hash ?? Keccak.OfAnEmptySequenceRlp,
                Keccak.OfAnEmptySequenceRlp,
                _signer.Address,
                parent.Difficulty,
                parent.Number + 1,
                parent.GasLimit,
                timestamp ?? parent.Timestamp,
                Bytes.Empty) {TotalDifficulty = parent.TotalDifficulty + parent.Difficulty};

            header.BaseFeePerGas = BaseFeeCalculator.Calculate(parent, _specProvider.GetSpec(header.Number));

            return new Block(header, new[] {transaction}, Array.Empty<BlockHeader>());
        }
    }
}
