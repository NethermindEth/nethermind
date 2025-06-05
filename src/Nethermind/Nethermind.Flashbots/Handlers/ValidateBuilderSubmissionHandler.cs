// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Flashbots.Data;
using Nethermind.Consensus;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.State;
using Nethermind.Core.Crypto;

namespace Nethermind.Flashbots.Handlers;

public class ValidateSubmissionHandler
{
    private ProcessingOptions ValidateSubmissionProcessingOptions = ProcessingOptions.ReadOnlyChain
         | ProcessingOptions.IgnoreParentNotOnMainChain
         | ProcessingOptions.ForceProcessing
         | ProcessingOptions.StoreReceipts;

    private readonly IBlockTree _blockTree;
    private readonly IHeaderValidator _headerValidator;
    private readonly IBlockValidator _blockValidator;
    private readonly ILogger _logger;
    private readonly IFlashbotsConfig _flashbotsConfig;
    private readonly ISpecProvider _specProvider;
    private readonly IEthereumEcdsa _ethereumEcdsa;
    private readonly IReadOnlyTxProcessingScope _processingScope;
    private readonly BlockProcessor _blockProcessor;

    public ValidateSubmissionHandler(
        IHeaderValidator headerValidator,
        IBlockTree blockTree,
        IBlockValidator blockValidator,
        IReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory,
        ILogManager logManager,
        ISpecProvider specProvider,
        IFlashbotsConfig flashbotsConfig,
        IEthereumEcdsa ethereumEcdsa)
    {
        _blockTree = blockTree;
        _blockValidator = blockValidator;
        _ethereumEcdsa = ethereumEcdsa;
        _flashbotsConfig = flashbotsConfig;
        _headerValidator = headerValidator;
        _logger = logManager!.GetClassLogger();
        _specProvider = specProvider;

        _processingScope = readOnlyTxProcessingEnvFactory.Create().Build(Keccak.EmptyTreeHash);
        var worldState = _processingScope.WorldState;
        ITransactionProcessor transactionProcessor = _processingScope.TransactionProcessor;
        IBlockCachePreWarmer preWarmer = new BlockCachePreWarmer(readOnlyTxProcessingEnvFactory, worldState, _specProvider, 0, logManager);
        _blockProcessor = new BlockProcessor(
            _specProvider,
            _blockValidator,
            new Consensus.Rewards.RewardCalculator(_specProvider),
            new BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, worldState),
            worldState,
            NullReceiptStorage.Instance,
            new BeaconBlockRootHandler(transactionProcessor, worldState),
            new BlockhashStore(_specProvider, worldState),
            logManager: logManager,
            withdrawalProcessor: new WithdrawalProcessor(worldState, logManager!),
            preWarmer: preWarmer,
            executionRequestsProcessor: new ExecutionRequestsProcessor(transactionProcessor));
    }

    public Task<ResultWrapper<FlashbotsResult>> ValidateSubmission(BuilderBlockValidationRequest request)
    {
        ExecutionPayloadV3 payload = request.ExecutionPayload.ToExecutionPayloadV3();

        if (request.ParentBeaconBlockRoot is null)
        {
            return FlashbotsResult.Invalid("Parent beacon block root must be set in the request");
        }

        payload.ParentBeaconBlockRoot = new Hash256(request.ParentBeaconBlockRoot);

        BlobsBundleV1 blobsBundle = request.BlobsBundle;

        string payloadStr = $"BuilderBlock: {payload}";

        if (_logger.IsInfo)
            _logger.Info($"blobs bundle blobs {blobsBundle.Blobs.Length} commits {blobsBundle.Commitments.Length} proofs {blobsBundle.Proofs.Length} commitments");

        BlockDecodingResult decodingResult = payload.TryGetBlock();
        Block? block = decodingResult.Block;
        if (block is null)
        {
            if (_logger.IsTrace) _logger.Trace($"Invalid block: {decodingResult.Error}. Result of {payloadStr}.");
            return FlashbotsResult.Invalid($"Block {payload} could not be parsed as a block: {decodingResult.Error}");
        }

        if (!ValidateBlock(block, request.Message, request.RegisteredGasLimit, out string? error))
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid block. Result of {payloadStr}. Error: {error}");
            return FlashbotsResult.Invalid(error ?? "Block validation failed");
        }

        if (!ValidateBlobsBundle(block.Transactions, blobsBundle, out string? blobsError))
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid blobs bundle. Result of {payloadStr}. Error: {blobsError}");
            return FlashbotsResult.Invalid(blobsError ?? "Blobs bundle validation failed");
        }

        return FlashbotsResult.Valid();
    }

    private bool ValidateBlock(Block block, BidTrace message, long registeredGasLimit, out string? error)
    {
        error = null;

        if (message.ParentHash != block.Header.ParentHash)
        {
            error = $"Parent hash mismatch. Expected {message.ParentHash} but got {block.Header.ParentHash}";
            return false;
        }

        if (message.BlockHash != block.Header.Hash)
        {
            error = $"Block hash mismatch. Expected {message.BlockHash} but got {block.Header.Hash}";
            return false;
        }

        if (message.GasLimit != block.GasLimit)
        {
            error = $"Gas limit mismatch. Expected {message.GasLimit} but got {block.GasLimit}";
            return false;
        }

        if (message.GasUsed != block.GasUsed)
        {
            error = $"Gas used mismatch. Expected {message.GasUsed} but got {block.GasUsed}";
            return false;
        }

        Address feeRecipient = message.ProposerFeeRecipient;
        UInt256 expectedProfit = message.Value;

        if (!ValidatePayload(block, feeRecipient, expectedProfit, registeredGasLimit, _flashbotsConfig.UseBalanceDiffProfit, _flashbotsConfig.ExcludeWithdrawals, out error))
        {
            return false;
        }

        _logger.Info($"Validated block Hash: {block.Header.Hash} Number: {block.Header.Number} ParentHash: {block.Header.ParentHash}");

        return true;
    }

    private bool ValidateBlobsBundle(Transaction[] transactions, BlobsBundleV1 blobsBundle, out string? error)
    {
        // get sum of length of blobs of each transaction
        int totalBlobsLength = transactions.Sum(t => t.BlobVersionedHashes is not null ? t.BlobVersionedHashes.Length : 0);

        if (totalBlobsLength != blobsBundle.Blobs.Length)
        {
            error = $"Total blobs length mismatch. Expected {totalBlobsLength} but got {blobsBundle.Blobs.Length}";
            return false;
        }

        if (totalBlobsLength != blobsBundle.Commitments.Length)
        {
            error = $"Total commitments length mismatch. Expected {totalBlobsLength} but got {blobsBundle.Commitments.Length}";
            return false;
        }

        if (totalBlobsLength != blobsBundle.Proofs.Length)
        {
            error = $"Total proofs length mismatch. Expected {totalBlobsLength} but got {blobsBundle.Proofs.Length}";
            return false;
        }

        if (!IBlobProofsManager.For(ProofVersion.V1).ValidateProofs(new ShardBlobNetworkWrapper(blobsBundle.Blobs, blobsBundle.Commitments, blobsBundle.Proofs, ProofVersion.V1)))
        {
            error = "Invalid KZG proofs";
            return false;
        }

        error = null;

        _logger.Info($"Validated blobs bundle with {totalBlobsLength} blobs, commitments: {blobsBundle.Commitments.Length}, proofs: {blobsBundle.Proofs.Length}");

        return true;
    }

    private bool ValidatePayload(Block block, Address feeRecipient, UInt256 expectedProfit, long registerGasLimit, bool useBalanceDiffProfit, bool excludeWithdrawals, out string? error)
    {
        BlockHeader? parentHeader = _blockTree.FindHeader(block.ParentHash!, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);

        if (parentHeader is null)
        {
            error = $"Parent header {block.ParentHash} not found";
            return false;
        }

        if (!ValidateBlockMetadata(block, registerGasLimit, parentHeader, out error))
        {
            return false;
        }

        using var scope = _processingScope;
        IWorldState worldState = _processingScope.WorldState;
        Hash256 stateRoot = parentHeader.StateRoot!;
        worldState.StateRoot = stateRoot;
        IReleaseSpec spec = _specProvider.GetSpec(parentHeader);

        RecoverSenderAddress(block, spec);
        UInt256 feeRecipientBalanceBefore = worldState.HasStateForRoot(stateRoot) ? (worldState.AccountExists(feeRecipient) ? worldState.GetBalance(feeRecipient) : UInt256.Zero) : UInt256.Zero;

        List<Block> suggestedBlocks = [block];
        BlockReceiptsTracer blockReceiptsTracer = new();

        try
        {
            if (!_flashbotsConfig.EnableValidation)
            {
                ValidateSubmissionProcessingOptions |= ProcessingOptions.NoValidation;
            }
            _ = _blockProcessor.Process(stateRoot, suggestedBlocks, ValidateSubmissionProcessingOptions, blockReceiptsTracer)[0];
        }
        catch (Exception e)
        {
            error = $"Block processing failed: {e.Message}";
            return false;
        }

        UInt256 feeRecipientBalanceAfter = worldState.GetBalance(feeRecipient);

        UInt256 amtBeforeOrWithdrawn = feeRecipientBalanceBefore;

        if (excludeWithdrawals)
        {
            foreach (Withdrawal withdrawal in block.Withdrawals ?? [])
            {
                if (withdrawal.Address == feeRecipient)
                {
                    amtBeforeOrWithdrawn += withdrawal.AmountInGwei;
                }
            }
        }

        if (!_blockValidator.ValidateSuggestedBlock(block, out error))
        {
            return false;
        }

        if (ValidateProposerPayment(expectedProfit, useBalanceDiffProfit, feeRecipientBalanceAfter, amtBeforeOrWithdrawn)) return true;

        if (!ValidateProcessedBlock(block, feeRecipient, expectedProfit, blockReceiptsTracer.TxReceipts, out error))
        {
            return false;
        }

        error = null;
        return true;
    }

    private void RecoverSenderAddress(Block block, IReleaseSpec spec)
    {
        foreach (Transaction tx in block.Transactions)
        {
            if (tx.SenderAddress is null)
            {
                tx.SenderAddress = _ethereumEcdsa.RecoverAddress(tx, !spec.ValidateChainId);
            }
        }
    }

    private bool ValidateBlockMetadata(Block block, long registerGasLimit, BlockHeader parentHeader, out string? error)
    {
        if (!_headerValidator.Validate(block.Header))
        {
            error = $"Invalid block header hash {block.Header.Hash}";
            return false;
        }

        if (block.Header.Number <= _blockTree.Head?.Number)
        {
            error = $"Block {block.Header.Number} is not better than head {_blockTree.Head?.Number}";
            return false;
        }

        if (block.Header.TotalDifficulty != null && !_blockTree.IsBetterThanHead(block.Header))
        {
            error = $"Block {block.Header.Hash} is not better than head {_blockTree.Head?.Hash}";
            return false;
        }

        long calculatedGasLimit = GetGasLimit(parentHeader, registerGasLimit);

        if (calculatedGasLimit != block.Header.GasLimit)
        {
            error = $"Gas limit mismatch. Expected {calculatedGasLimit} but got {block.Header.GasLimit}";
            return false;
        }
        error = null;
        return true;
    }

    private long GetGasLimit(BlockHeader parentHeader, long desiredGasLimit)
    {
        long parentGasLimit = parentHeader.GasLimit;
        long gasLimit = parentGasLimit;

        long? targetGasLimit = desiredGasLimit;
        long newBlockNumber = parentHeader.Number + 1;
        IReleaseSpec spec = _specProvider.GetSpec(newBlockNumber, parentHeader.Timestamp);
        if (targetGasLimit is not null)
        {
            long maxGasLimitDifference = Math.Max(0, parentGasLimit / spec.GasLimitBoundDivisor - 1);
            gasLimit = targetGasLimit.Value > parentGasLimit
                ? parentGasLimit + Math.Min(targetGasLimit.Value - parentGasLimit, maxGasLimitDifference)
                : parentGasLimit - Math.Min(parentGasLimit - targetGasLimit.Value, maxGasLimitDifference);
        }

        gasLimit = Eip1559GasLimitAdjuster.AdjustGasLimit(spec, gasLimit, newBlockNumber);
        return gasLimit;
    }

    private bool ValidateProposerPayment(UInt256 expectedProfit, bool useBalanceDiffProfit, UInt256 feeRecipientBalanceAfter, UInt256 amtBeforeOrWithdrawn)
    {
        // validate proposer payment

        if (useBalanceDiffProfit && feeRecipientBalanceAfter >= amtBeforeOrWithdrawn)
        {
            UInt256 feeRecipientBalanceDelta = feeRecipientBalanceAfter - amtBeforeOrWithdrawn;
            if (feeRecipientBalanceDelta >= expectedProfit)
            {
                if (feeRecipientBalanceDelta > expectedProfit)
                {
                    _logger.Warn($"Builder claimed profit is lower than calculated profit. Expected {expectedProfit} but actual {feeRecipientBalanceDelta}");
                }
                return true;
            }
            _logger.Warn($"Proposer payment is not enough, trying last tx payment validation, expected: {expectedProfit}, actual: {feeRecipientBalanceDelta}");
        }

        return false;
    }

    private bool ValidateProcessedBlock(Block processedBlock, Address feeRecipient, UInt256 expectedProfit, IReadOnlyList<TxReceipt> receipts, out string? error)
    {
        if (receipts.Count == 0)
        {
            error = "No proposer payment receipt";
            return false;
        }

        TxReceipt lastReceipt = receipts[^1];

        if (lastReceipt.StatusCode != StatusCode.Success)
        {
            error = $"Proposer payment failed ";
            return false;
        }

        int txIndex = lastReceipt.Index;

        if (txIndex + 1 != processedBlock.Transactions.Length)
        {
            error = $"Proposer payment index not last transaction in the block({txIndex} of {processedBlock.Transactions.Length - 1})";
            return false;
        }

        Transaction paymentTx = processedBlock.Transactions[txIndex];

        if (paymentTx.To != feeRecipient)
        {
            error = $"Proposer payment transaction recipient is not the proposer,received {paymentTx.To} expected {feeRecipient}";
            return false;
        }

        if (paymentTx.Value != expectedProfit)
        {
            error = $"Proposer payment transaction value is not the expected profit, received {paymentTx.Value} expected {expectedProfit}";
            return false;
        }

        if (paymentTx.Data != null && paymentTx.Data.Value.Length != 0)
        {
            error = "Proposer payment transaction data is not empty";
            return false;
        }

        if (paymentTx.MaxFeePerGas != processedBlock.BaseFeePerGas)
        {
            error = "Malformed proposer payment, max fee per gas not equal to block base fee per gas";
            return false;
        }

        if (paymentTx.MaxPriorityFeePerGas != processedBlock.BaseFeePerGas && paymentTx.MaxPriorityFeePerGas != 0)
        {
            error = "Malformed proposer payment, max priority fee per gas not equal to block max priority fee per gas";
            return false;
        }


        error = null;
        return true;
    }
}
