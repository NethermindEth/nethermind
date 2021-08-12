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
            IBlockProcessor.IBlockTransactionsExecutor blockTransactionsExecutor,
            IStateProvider stateProvider,
            IStorageProvider storageProvider,
            IReceiptStorage receiptStorage,
            ILogManager logManager,
            IBlockTree blockTree,
            ITxFilter? txFilter = null,
            AuRaContractGasLimitOverride? gasLimitOverride = null)
            : base(
                specProvider,
                blockValidator,
                rewardCalculator,
                blockTransactionsExecutor,
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
            if (blockTransactionsExecutor is IBlockProductionTransactionsExecutor produceBlockTransactionsStrategy)
            {
                produceBlockTransactionsStrategy.AddingTransaction += OnAddingTransaction;
            }
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
            TxReceipt[] receipts = base.ProcessBlock(block, blockTracer, options);
            AuRaValidator.OnBlockProcessingEnd(block, receipts, options);
            Metrics.AuRaStep = block.Header?.AuRaStep ?? 0;
            return receipts;
        }

        // This validations cannot be run in AuraSealValidator because they are dependent on state.
        private void ValidateAuRa(Block block)
        {
            if (!block.IsGenesis)
            {
                ValidateGasLimit(block);
                ValidateTxs(block);
            }
        }

        private BlockHeader GetParentHeader(Block block) => 
            _blockTree.FindParentHeader(block.Header, BlockTreeLookupOptions.None)!;

        private void ValidateGasLimit(Block block)
        {
            BlockHeader parentHeader = GetParentHeader(block);
            long? expectedGasLimit = null;
            if (_gasLimitOverride?.IsGasLimitValid(parentHeader, block.GasLimit, out expectedGasLimit) == false)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid gas limit for block {block.Number}, hash {block.Hash}, expected value from contract {expectedGasLimit}, but found {block.GasLimit}.");
                throw new InvalidBlockException(block.Hash);
            }
        }

        private void ValidateTxs(Block block)
        {
            for (int i = 0; i < block.Transactions.Length; i++)
            {
                Transaction tx = block.Transactions[i];
                AddingTxEventArgs args = CheckTxPosdaoRules(new AddingTxEventArgs(i, tx, block, block.Transactions));
                if (args.Action != TxAction.Add)
                {
                    if (_logger.IsWarn) _logger.Warn($"Proposed block is not valid {block.ToString(Block.Format.FullHashAndNumber)}. {tx.ToShortString()} doesn't have required permissions. Reason: {args.Reason}.");
                    throw new InvalidBlockException(block.Hash);
                }
            }
        }
        
        private void OnAddingTransaction(object? sender, AddingTxEventArgs e)
        {
            CheckTxPosdaoRules(e);
        }
        
        private AddingTxEventArgs CheckTxPosdaoRules(AddingTxEventArgs args)
        {
            (bool Allowed, string Reason)? TryRecoverSenderAddress(Transaction tx, BlockHeader header)
            {
                if (tx.Signature != null)
                {
                    IReleaseSpec spec = _specProvider.GetSpec(args.Block.Number);
                    EthereumEcdsa ecdsa = new(_specProvider.ChainId, LimboLogs.Instance);
                    Address txSenderAddress = ecdsa.RecoverAddress(tx, !spec.ValidateChainId);
                    if (tx.SenderAddress != txSenderAddress)
                    {
                        if (_logger.IsWarn) _logger.Warn($"Transaction {tx.ToShortString()} in block {args.Block.ToString(Block.Format.FullHashAndNumber)} had recovered sender address on validation.");
                        tx.SenderAddress = txSenderAddress;
                        return _txFilter.IsAllowed(tx, header);
                    }
                }

                return null;
            }

            BlockHeader parentHeader = GetParentHeader(args.Block);
            (bool Allowed, string Reason) txFilterResult = _txFilter.IsAllowed(args.Transaction, parentHeader);
            if (!txFilterResult.Allowed)
            {
                txFilterResult = TryRecoverSenderAddress(args.Transaction, parentHeader) ?? txFilterResult;
            }

            if (!txFilterResult.Allowed)
            {
                args.Set(TxAction.Skip, txFilterResult.Reason);
            }

            return args;
        }

        private class NullAuRaValidator : IAuRaValidator
        {
            public Address[] Validators => Array.Empty<Address>();
            public void OnBlockProcessingStart(Block block, ProcessingOptions options = ProcessingOptions.None) { }
            public void OnBlockProcessingEnd(Block block, TxReceipt[] receipts, ProcessingOptions options = ProcessingOptions.None) { }
        }
    }
}
