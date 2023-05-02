// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
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
        private readonly ContractRewriter? _contractRewriter;
        private readonly ITxFilter _txFilter;
        private readonly ILogger _logger;
        private IAuRaValidator? _auRaValidator;

        public AuRaBlockProcessor(
            ISpecProvider specProvider,
            IBlockValidator blockValidator,
            IRewardCalculator rewardCalculator,
            IBlockProcessor.IBlockTransactionsExecutor blockTransactionsExecutor,
            IWorldState worldState,
            IReceiptStorage receiptStorage,
            ILogManager logManager,
            IBlockTree blockTree,
            IWithdrawalProcessor withdrawalProcessor,
            ITxFilter? txFilter = null,
            AuRaContractGasLimitOverride? gasLimitOverride = null,
            ContractRewriter? contractRewriter = null)
            : base(
                specProvider,
                blockValidator,
                rewardCalculator,
                blockTransactionsExecutor,
                worldState,
                receiptStorage,
                NullWitnessCollector.Instance,
                logManager,
                withdrawalProcessor)
        {
            _specProvider = specProvider;
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _logger = logManager?.GetClassLogger<AuRaBlockProcessor>() ?? throw new ArgumentNullException(nameof(logManager));
            _txFilter = txFilter ?? NullTxFilter.Instance;
            _gasLimitOverride = gasLimitOverride;
            _contractRewriter = contractRewriter;
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
            _contractRewriter?.RewriteContracts(block.Number, _stateProvider, _specProvider.GetSpec(block.Header));
            AuRaValidator.OnBlockProcessingStart(block, options);
            TxReceipt[] receipts = base.ProcessBlock(block, blockTracer, options);
            AuRaValidator.OnBlockProcessingEnd(block, receipts, options);
            Metrics.AuRaStep = block.Header?.AuRaStep ?? 0;
            return receipts;
        }

        // After PoS switch we need to revert to standard block processing, ignoring AuRa customizations
        protected TxReceipt[] PostMergeProcessBlock(Block block, IBlockTracer blockTracer, ProcessingOptions options)
        {
            return base.ProcessBlock(block, blockTracer, options);
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
            if (_gasLimitOverride?.IsGasLimitValid(parentHeader, block.GasLimit, out long? expectedGasLimit) == false)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid gas limit for block {block.Number}, hash {block.Hash}, expected value from contract {expectedGasLimit}, but found {block.GasLimit}.");
                throw new InvalidBlockException(block);
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
                    throw new InvalidBlockException(block);
                }
            }
        }

        private void OnAddingTransaction(object? sender, AddingTxEventArgs e)
        {
            CheckTxPosdaoRules(e);
        }

        private AddingTxEventArgs CheckTxPosdaoRules(AddingTxEventArgs args)
        {
            AcceptTxResult? TryRecoverSenderAddress(Transaction tx, BlockHeader header)
            {
                if (tx.Signature is not null)
                {
                    IReleaseSpec spec = _specProvider.GetSpec(args.Block.Header);
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
            AcceptTxResult isAllowed = _txFilter.IsAllowed(args.Transaction, parentHeader);
            if (!isAllowed)
            {
                isAllowed = TryRecoverSenderAddress(args.Transaction, parentHeader) ?? isAllowed;
            }

            if (!isAllowed)
            {
                args.Set(TxAction.Skip, isAllowed.ToString());
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
