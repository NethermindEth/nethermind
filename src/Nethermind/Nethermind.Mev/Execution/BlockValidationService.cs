// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Mev.Data;
using System;
using System.Collections.Generic;
using Nethermind.State;
using Nethermind.Blockchain;
using Nethermind.State.Proofs;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;

namespace Nethermind.Mev.Execution;

public class BlockValidationService
{
    private readonly IBlockFinder _blockFinder;
    private readonly IBlockProcessor _blockProcessor;
    private readonly ISpecProvider _specProvider;
    private readonly IGasLimitCalculator _gasLimitCalculator;
    private readonly IHeaderValidator _headerValidator;
    private readonly ILogger _logger;

    public BlockValidationService(
        IBlockProcessor blockProcessor,
        ITransactionProcessor transactionProcessor,
        IStateProvider stateProvider,
        IBlockFinder blockFinder,
        ISpecProvider specProvider,
        IGasLimitCalculator gasLimitCalculator,
        IHeaderValidator headerValidator,
        ILogManager logManager)
    {
        _blockProcessor = blockProcessor ?? throw new ArgumentNullException(nameof(blockProcessor));
        _logger = logManager?.GetClassLogger<BlockValidationService>() ?? throw new ArgumentNullException(nameof(logManager));
        _blockFinder = blockFinder;
        _specProvider = specProvider;
        _gasLimitCalculator = gasLimitCalculator;
        _headerValidator = headerValidator;
    }

    public void ValidateBuilderSubmission(BuilderBlockValidationRequest request, bool v2 = false)
    {
        if (request.Message is null)
        {
            throw new InvalidOperationException("Message is null");
        }

        if (request.ExecutionPayload is null)
        {
            throw new InvalidOperationException("Execution Payload is null");
        }

        if (!request.ExecutionPayload.TryGetBlock(out Block? builderBlock))
        {
            throw new InvalidOperationException("Execution Payload failed to be converted to Block");
        }

        IReleaseSpec spec = _specProvider.GetSpec(builderBlock.Header);

        if (v2)
            VerifyWithdrawals(builderBlock.Withdrawals, request.WithdrawalsRoot, spec.WithdrawalsEnabled);
        VerifyBlockHeader(builderBlock, request.Message);
        ValidatePayload(builderBlock, request.Message.ProposerFeeRecipient, request.Message.Value, request.RegisteredGasLimit);

        if (_logger.IsInfo) _logger.Info($"Validated block: hash {builderBlock.Hash}, number {builderBlock.Number}, parentHash {builderBlock.ParentHash}");
    }

    private static void VerifyBlockHeader(Block block, BidTrace message)
    {
        if (block.Hash != message.BlockHash)
        {
            throw new InvalidOperationException($"Block hash mismatch: expected {message.BlockHash}, got {block.Hash}");
        }

        if (block.ParentHash != message.ParentHash)
        {
            throw new InvalidOperationException($"Parent hash mismatch: expected {message.ParentHash}, got {block.ParentHash}");
        }

        if (block.GasLimit != message.GasLimit)
        {
            throw new InvalidOperationException($"Gas limit mismatch: expected {message.GasLimit}, got {block.GasLimit}");
        }

        if (block.GasUsed != message.GasUsed)
        {
            throw new InvalidOperationException($"Gas used mismatch: expected {message.GasUsed}, got {block.GasUsed}");
        }
    }

    private void ValidatePayload(Block block, Address? feeRecipient, UInt256 expectedProfit, uint registeredGasLimit)
    {
        if (block.ParentHash is null)
        {
            throw new InvalidOperationException($"ParentHash provided is null!");
        }

        Block? parentBlock = _blockFinder.FindBlock(block.ParentHash, BlockTreeLookupOptions.None)
            ?? throw new InvalidOperationException($"Parent block with hash {block.ParentHash} not found.");

        if (parentBlock.StateRoot is null)
        {
            throw new InvalidOperationException($"Block simulation failed. Parent Block StateRoot was null");
        }

        if (!_headerValidator.Validate(block.Header, parentBlock.Header, false))
        {
            throw new InvalidOperationException("Block Header is not valid!");
        }

        long calculatedGasLimit = _gasLimitCalculator.GetGasLimit(parentBlock.Header, registeredGasLimit);
        if (calculatedGasLimit != block.GasLimit)
        {
            throw new InvalidOperationException($"Gas limit mismatch: expected {calculatedGasLimit}, got {block.GasLimit}");
        }

        List<Block> blocks = new()
        {
            block
        };
        BlockReceiptsTracer tracer = new();
        Block[] processed = _blockProcessor.Process(parentBlock.StateRoot, blocks, ProcessingOptions.ReadOnlyChain & ProcessingOptions.DoNotUpdateHead, tracer);

        block = processed[0];
        IReadOnlyList<TxReceipt> receipts = tracer.TxReceipts;

        if (receipts.Count == 0)
        {
            throw new InvalidOperationException("No proposer payment receipt");
        }

        TxReceipt lastReceipt = receipts[^1];

        if (lastReceipt.StatusCode != 1)
        {
            throw new InvalidOperationException("Proposer payment not successful");
        }

        Transaction? paymentTx = null;
        for (int i = 0; i < block.Transactions.Length; i++)
        {
            if (block.Transactions[i].Hash == lastReceipt.TxHash)
            {
                paymentTx = block.Transactions[i];
                if (i + 1 != block.Transactions.Length)
                {
                    throw new InvalidOperationException($"Proposer payment index not last transaction in the block ({i} of {block.Transactions.Length - 1})");
                }
                break;
            }
        }

        if (paymentTx == null)
        {
            throw new InvalidOperationException("Payment tx not in the block");
        }

        Address? paymentTo = paymentTx.To;

        if (paymentTo == null || paymentTo != feeRecipient)
        {
            throw new InvalidOperationException($"Payment tx not to the proposer's fee recipient ({paymentTo})");
        }

        if (paymentTx.Value != expectedProfit)
        {
            throw new InvalidOperationException($"Inaccurate payment {paymentTx.Value}, expected {expectedProfit}");
        }

        if (paymentTx.Data is not null && paymentTx.Data.Length > 0)
        {
            throw new InvalidOperationException("Malformed proposer payment, contains calldata");
        }

        if (paymentTx.GasPrice != block.Header.BaseFeePerGas)
        {
            throw new InvalidOperationException("Malformed proposer payment, gas price not equal to base fee");
        }

        if (paymentTx.MaxPriorityFeePerGas != block.Header.BaseFeePerGas && paymentTx.MaxPriorityFeePerGas != 0)
        {
            throw new InvalidOperationException("Malformed proposer payment, unexpected max priority fee");
        }

        if (paymentTx.MaxFeePerGas != block.Header.BaseFeePerGas)
        {
            throw new InvalidOperationException("Malformed proposer payment, unexpected max fee");
        }

    }

    public static void VerifyWithdrawals(Withdrawal[]? withdrawals, Keccak? expectedWithdrawalsRoot, bool withdrawalsEnabled)
    {
        if (!withdrawalsEnabled)
        {
            if (withdrawals != null || expectedWithdrawalsRoot is null)
            {
                throw new InvalidOperationException("Withdrawals not enabled.");
            }
            return;
        }
        Keccak withdrawalsRoot = new WithdrawalTrie(withdrawals!).RootHash;
        if (!withdrawalsRoot.Equals(expectedWithdrawalsRoot))
        {
            throw new InvalidOperationException("Withdrawals mismatch.");
        }
    }

}
