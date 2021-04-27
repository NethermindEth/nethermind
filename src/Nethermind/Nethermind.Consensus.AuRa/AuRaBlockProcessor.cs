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

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Consensus.AuRa
{
    public class AuRaBlockProcessor : BlockProcessor
    {
        private readonly ISpecProvider _specProvider;
        private readonly IBlockTree _blockTree;
        private readonly AuRaContractGasLimitOverride? _gasLimitOverride;
        private readonly ITxFilter _txFilter;
        private readonly ILogger _logger;
        private IAuRaValidator? _auRaValidator;

        public AuRaBlockProcessor(
            ISpecProvider specProvider,
            IBlockValidator blockValidator,
            IRewardCalculator rewardCalculator,
            ITransactionProcessor transactionProcessor,
            IStateProvider stateProvider,
            IStorageProvider storageProvider,
            ITxPool txPool,
            IReceiptStorage receiptStorage,
            ILogManager logManager,
            IBlockTree blockTree,
            ITxFilter? txFilter = null,
            AuRaContractGasLimitOverride? gasLimitOverride = null)
            : base(
                specProvider,
                blockValidator,
                rewardCalculator,
                transactionProcessor,
                stateProvider,
                storageProvider,
                receiptStorage,
                NullWitnessCollector.Instance, // TODO: we will not support beam sync on AuRa chains for now
                logManager)
        {
            _specProvider = specProvider;
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _logger = logManager?.GetClassLogger<AuRaBlockProcessor>() ?? throw new ArgumentNullException(nameof(logManager));
            _txFilter = txFilter ?? NullTxFilter.Instance;
            _gasLimitOverride = gasLimitOverride;
        }

        public IAuRaValidator AuRaValidator
        {
            get => _auRaValidator ?? new NullAuRaValidator();
            set => _auRaValidator = value;
        }

        protected override TxReceipt[] ProcessBlock(Block block, IBlockTracer blockTracer, ProcessingOptions options)
        {
            ValidateAuRa(block);
            AuRaValidator.OnBlockProcessingStart(block, options);
            var receipts = base.ProcessBlock(block, blockTracer, options);
            AuRaValidator.OnBlockProcessingEnd(block, receipts, options);
            Metrics.AuRaStep = block.Header?.AuRaStep ?? 0;
            return receipts;
        }

        // This validations cannot be run in AuraSealValidator because they are dependent on state.
        private void ValidateAuRa(Block block)
        {
            if (!block.IsGenesis)
            {
                var parentHeader = _blockTree.FindParentHeader(block.Header, BlockTreeLookupOptions.None);
                
                ValidateGasLimit(block.Header, parentHeader);
                ValidateTxs(block, parentHeader);
            }
        }

        private void ValidateGasLimit(BlockHeader header, BlockHeader parentHeader)
        {
            long? expectedGasLimit = null;
            if (_gasLimitOverride?.IsGasLimitValid(parentHeader, header.GasLimit, out expectedGasLimit) == false)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid gas limit for block {header.Number}, hash {header.Hash}, expected value from contract {expectedGasLimit}, but found {header.GasLimit}.");
                throw new InvalidBlockException(header.Hash);
            }
        }

        private void ValidateTxs(Block block, BlockHeader parentHeader)
        {
            (bool Allowed, string Reason)? TryRecoverSenderAddress(Transaction tx)
            {
                if (tx.Signature != null)
                {
                    IReleaseSpec spec = _specProvider.GetSpec(block.Number);
                    var ecdsa = new EthereumEcdsa(_specProvider.ChainId, LimboLogs.Instance);
                    Address txSenderAddress = ecdsa.RecoverAddress(tx, !spec.ValidateChainId);
                    if (tx.SenderAddress != txSenderAddress)
                    {
                        if (_logger.IsWarn) _logger.Warn($"Transaction {tx.ToShortString()} in block {block.ToString(Block.Format.FullHashAndNumber)} had recovered sender address on validation.");
                        tx.SenderAddress = txSenderAddress;
                        return _txFilter.IsAllowed(tx, parentHeader);
                    }
                }

                return null;
            }

            for (int i = 0; i < block.Transactions.Length; i++)
            {
                var tx = block.Transactions[i];
                var txFilterResult = _txFilter.IsAllowed(tx, parentHeader);
                if (!txFilterResult.Allowed)
                {
                    txFilterResult = TryRecoverSenderAddress(tx) ?? txFilterResult;
                }

                if (!txFilterResult.Allowed)
                {
                    if (_logger.IsWarn) _logger.Warn($"Proposed block is not valid {block.ToString(Block.Format.FullHashAndNumber)}. {tx.ToShortString()} doesn't have required permissions. Reason: {txFilterResult.Reason}.");
                    throw new InvalidBlockException(block.Hash);
                }
            }
        }
        
        private class NullAuRaValidator : IAuRaValidator
        {
            public Address[] Validators => Array.Empty<Address>();
            public void OnBlockProcessingStart(Block block, ProcessingOptions options = ProcessingOptions.None) { }
            public void OnBlockProcessingEnd(Block block, TxReceipt[] receipts, ProcessingOptions options = ProcessingOptions.None) { }
        }
    }
}
