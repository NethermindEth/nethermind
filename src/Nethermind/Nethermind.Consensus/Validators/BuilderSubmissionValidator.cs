// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using System;
using System.Collections.Generic;
using Nethermind.State;
using Nethermind.Blockchain;
using Nethermind.State.Proofs;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Blockchain.Receipts;

namespace Nethermind.Consensus.Validators;

public class BuilderSubmissionValidator : IBuilderSubmissionValidator
{
    private readonly ReadOnlyTxProcessingEnvFactory _readOnlyTxProcessingEnvFactory;
    private readonly BlockProducerTransactionsExecutorFactory _transactionsExecutorFactory;
    private readonly IBlockValidator _blockValidator;
    private readonly IRewardCalculatorSource _rewardCalculatorSource;
    private readonly IReceiptStorage _receiptStorage;
    private readonly ISpecProvider _specProvider;
    private readonly IGasLimitCalculator _gasLimitCalculator;
    private readonly IHeaderValidator _headerValidator;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;

    public BuilderSubmissionValidator(
        ISpecProvider specProvider,
        IGasLimitCalculator gasLimitCalculator,
        IHeaderValidator headerValidator,
        ReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory,
        IReceiptStorage receiptStorage,
        IRewardCalculatorSource rewardCalculatorSource,
        IBlockValidator blockValidator,
        ILogManager logManager)
    {
        _logger = logManager?.GetClassLogger<BuilderSubmissionValidator>() ?? throw new ArgumentNullException(nameof(logManager));
        _specProvider = specProvider;
        _gasLimitCalculator = gasLimitCalculator;
        _headerValidator = headerValidator;
        _logManager = logManager;
        _readOnlyTxProcessingEnvFactory = readOnlyTxProcessingEnvFactory;
        _receiptStorage = receiptStorage;
        _rewardCalculatorSource = rewardCalculatorSource;
        _blockValidator = blockValidator;
        _transactionsExecutorFactory = new(specProvider!, logManager); ;
    }

    public void ValidateBuilderSubmission(Block builderBlock, BidTrace message, uint registeredGasLimit,
        Keccak? withdrawalsRoot = null)
    {
        IReleaseSpec spec = _specProvider.GetSpec(builderBlock.Header);

        if (withdrawalsRoot is not null || builderBlock.Withdrawals is not null)
            VerifyWithdrawals(builderBlock.Withdrawals, withdrawalsRoot, spec.WithdrawalsEnabled);
        VerifyBlockHeader(builderBlock, message);
        ValidateBlock(builderBlock, message.ProposerFeeRecipient, message.Value, registeredGasLimit);

        if (_logger.IsInfo) _logger.Info($"Validated block: hash {builderBlock.Hash}, number {builderBlock.Number}, parentHash {builderBlock.ParentHash}");
    }

    private static void VerifyBlockHeader(Block block, BidTrace message)
    {
        if (block.Hash != message.BlockHash)
            throw new InvalidOperationException($"Block hash mismatch: expected {message.BlockHash}, got {block.Hash}");

        if (block.ParentHash != message.ParentHash)
            throw new InvalidOperationException($"Parent hash mismatch: expected {message.ParentHash}, got {block.ParentHash}");

        if (block.GasLimit != message.GasLimit)
            throw new InvalidOperationException($"Gas limit mismatch: expected {message.GasLimit}, got {block.GasLimit}");

        if (block.GasUsed != message.GasUsed)
            throw new InvalidOperationException($"Gas used mismatch: expected {message.GasUsed}, got {block.GasUsed}");
    }

    private void ValidateBlock(Block block, Address? feeRecipient, UInt256 expectedProfit, uint registeredGasLimit)
    {
        if (block.ParentHash is null)
            throw new InvalidOperationException($"ParentHash provided is null!");

        ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv = _readOnlyTxProcessingEnvFactory.Create();

        Block? parentBlock = readOnlyTxProcessingEnv.BlockTree.FindBlock(block.ParentHash, BlockTreeLookupOptions.None)
            ?? throw new InvalidOperationException($"Parent block with hash {block.ParentHash} not found.");

        if (parentBlock.StateRoot is null)
            throw new InvalidOperationException($"Block simulation failed. Parent Block StateRoot was null");

        if (!_headerValidator.Validate(block.Header, parentBlock.Header, false))
            throw new InvalidOperationException("Block Header is not valid!");

        long calculatedGasLimit = _gasLimitCalculator.GetGasLimit(parentBlock.Header, registeredGasLimit);
        if (calculatedGasLimit != block.GasLimit)
            throw new InvalidOperationException($"Gas limit mismatch: expected {calculatedGasLimit}, got {block.GasLimit}");

        List<Block> blocks = new()
        {
            block
        };
        BlockReceiptsTracer tracer = new();

        BlockProcessor blockProcessor =
            new(_specProvider,
            _blockValidator,
            _rewardCalculatorSource.Get(readOnlyTxProcessingEnv.TransactionProcessor),
            _transactionsExecutorFactory.Create(readOnlyTxProcessingEnv),
            readOnlyTxProcessingEnv.StateProvider,
            readOnlyTxProcessingEnv.StorageProvider,
            _receiptStorage,
            NullWitnessCollector.Instance,
            _logManager,
            new BlockProductionWithdrawalProcessor(new WithdrawalProcessor(readOnlyTxProcessingEnv.StateProvider, _logManager)));


        Block[] processed = blockProcessor.Process(parentBlock.StateRoot, blocks, ProcessingOptions.ReadOnlyChain & ProcessingOptions.DoNotUpdateHead, tracer);

        block = processed[0];
        IReadOnlyList<TxReceipt> receipts = tracer.TxReceipts;

        if (receipts.Count == 0)
            throw new InvalidOperationException("No proposer payment receipt");

        TxReceipt lastReceipt = receipts[^1];

        if (lastReceipt.StatusCode != 1)
            throw new InvalidOperationException("Proposer payment not successful");

        Transaction? paymentTx = null;
        for (int i = 0; i < block.Transactions.Length; i++)
        {
            if (block.Transactions[i].Hash == lastReceipt.TxHash)
            {
                paymentTx = block.Transactions[i];
                if (i + 1 != block.Transactions.Length)
                    throw new InvalidOperationException($"Proposer payment index not last transaction in the block ({i} of {block.Transactions.Length - 1})");
                break;
            }
        }

        if (paymentTx == null)
            throw new InvalidOperationException("Payment tx not in the block");

        Address? paymentTo = paymentTx.To;

        if (paymentTo == null || paymentTo != feeRecipient)
            throw new InvalidOperationException($"Payment tx not to the proposer's fee recipient ({paymentTo})");

        if (paymentTx.Value != expectedProfit)
            throw new InvalidOperationException($"Inaccurate payment {paymentTx.Value}, expected {expectedProfit}");

        if (paymentTx.Data is not null && paymentTx.Data.Length > 0)
            throw new InvalidOperationException("Malformed proposer payment, contains calldata");

        if (paymentTx.GasPrice != block.Header.BaseFeePerGas)
            throw new InvalidOperationException("Malformed proposer payment, gas price not equal to base fee");

        if (paymentTx.MaxPriorityFeePerGas != block.Header.BaseFeePerGas && paymentTx.MaxPriorityFeePerGas != 0)
            throw new InvalidOperationException("Malformed proposer payment, unexpected max priority fee");

        if (paymentTx.MaxFeePerGas != block.Header.BaseFeePerGas)
            throw new InvalidOperationException("Malformed proposer payment, unexpected max fee");

    }

    public static void VerifyWithdrawals(Withdrawal[]? withdrawals, Keccak? expectedWithdrawalsRoot, bool withdrawalsEnabled)
    {
        if (!withdrawalsEnabled)
        {
            if (withdrawals != null || expectedWithdrawalsRoot is null)
                throw new InvalidOperationException("Withdrawals not enabled.");
            return;
        }
        Keccak withdrawalsRoot = new WithdrawalTrie(withdrawals!).RootHash;
        if (!withdrawalsRoot.Equals(expectedWithdrawalsRoot))
            throw new InvalidOperationException("Withdrawals mismatch.");
    }

}
