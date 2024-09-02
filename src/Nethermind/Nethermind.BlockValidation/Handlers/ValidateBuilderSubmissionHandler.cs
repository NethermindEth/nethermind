// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.BlockValidation.Data;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.State;

namespace Nethermind.BlockValidation.Handlers;

public class ValidateSubmissionHandler
{
    private readonly ReadOnlyTxProcessingEnv _txProcessingEnv;
    private readonly IBlockTree _blockTree;
    private readonly IBlockValidator _blockValidator;
    private readonly IGasLimitCalculator _gasLimitCalculator;
    private readonly ILogger _logger;

    public ValidateSubmissionHandler(
        IBlockValidator blockValidator,
        ReadOnlyTxProcessingEnv txProcessingEnv,
        IGasLimitCalculator gasLimitCalculator)
    {
        _blockValidator = blockValidator;
        _txProcessingEnv = txProcessingEnv;
        _blockTree = _txProcessingEnv.BlockTree;
        _gasLimitCalculator = gasLimitCalculator;
        _logger = txProcessingEnv.LogManager!.GetClassLogger();
    }

    private bool ValidateBlobsBundle(Transaction[] transactions, BlobsBundleV1 blobsBundle, out string? error)
    {
        // get sum of length of blobs of each transaction
        int totalBlobsLength = transactions.Sum(t => t.BlobVersionedHashes!.Length);

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

        if (!KzgPolynomialCommitments.AreProofsValid(blobsBundle.Proofs, blobsBundle.Commitments, blobsBundle.Blobs))
        {
            error = "Invalid KZG proofs";
            return false;
        }

        error = null;

        _logger.Info($"Validated blobs bundle with {totalBlobsLength} blobs, commitments: {blobsBundle.Commitments.Length}, proofs: {blobsBundle.Proofs.Length}");

        return true;
    }

    private BlockProcessor CreateBlockProcessor(IWorldState stateProvider)
    {
        return new BlockProcessor(
            _txProcessingEnv.SpecProvider,
            _blockValidator,
            new Consensus.Rewards.RewardCalculator(_txProcessingEnv.SpecProvider),
            new BlockProcessor.BlockValidationTransactionsExecutor(_txProcessingEnv.TransactionProcessor, stateProvider),
            stateProvider,
            new InMemoryReceiptStorage(),
            new BlockhashStore(_txProcessingEnv.SpecProvider, stateProvider),
            _txProcessingEnv.LogManager,
            new WithdrawalProcessor(stateProvider, _txProcessingEnv.LogManager!),
            new ReceiptsRootCalculator()
        );
    }

    private bool ValidatePayload(Block block, Address feeRecipient, UInt256 expectedProfit, long registerGasLimit, out string? error)
    {
        if(!HeaderValidator.ValidateHash(block.Header)){
            error = $"Invalid block header hash {block.Header.Hash}";
            return false;
        }

        if(!_blockTree.IsBetterThanHead(block.Header)){
            error = $"Block {block.Header.Hash} is not better than head";
            return false;
        }

        BlockHeader? parentHeader = _blockTree.FindHeader(block.ParentHash!, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);

        if (parentHeader is null){
            error = $"Parent header {block.ParentHash} not found";
            return false;
        }

        long calculatedGasLimit = _gasLimitCalculator.GetGasLimit(parentHeader, registerGasLimit);

        if (calculatedGasLimit != block.Header.GasLimit){
            error = $"Gas limit mismatch. Expected {calculatedGasLimit} but got {block.Header.GasLimit}";
            return false;
        }

        IReadOnlyTxProcessingScope processingScope = _txProcessingEnv.Build(parentHeader.StateRoot!);
        IWorldState currentState = processingScope.WorldState;

        UInt256 feeRecipientBalanceBefore = currentState.GetBalance(feeRecipient);

        BlockProcessor blockProcessor = CreateBlockProcessor(currentState);


        UInt256 feeRecipientBalanceAfter = currentState.GetBalance(feeRecipient);

        error = null;
        return true;
    }

    private bool ValidateBlock(Block block, BidTrace message, long registerGasLimit, out string? error)
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

        if (!ValidatePayload(block, feeRecipient, expectedProfit, registerGasLimit, out error))
        {
            return false;
        }

        _logger.Info($"Validated block Hash: {block.Header.Hash} Number: {block.Header.Number} ParentHash: {block.Header.ParentHash}");

        return true;
    }

    public Task<ResultWrapper<BlockValidationResult>> ValidateSubmission(BuilderBlockValidationRequest request)
    {
        ExecutionPayload payload = request.BlockRequest.ExecutionPayload;
        BlobsBundleV1 blobsBundle = request.BlockRequest.BlobsBundle;

        string payloadStr = $"BuilderBlock: {payload}";

        _logger.Info($"blobs bundle blobs {blobsBundle.Blobs.Length} commits {blobsBundle.Commitments.Length} proofs {blobsBundle.Proofs.Length}");

        if (!payload.TryGetBlock(out Block? block))
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid block. Result of {payloadStr}.");
            return BlockValidationResult.Invalid($"Block {payload} coud not be parsed as a block");
        }

        if (block is not null && !ValidateBlock(block, request.BlockRequest.Message, request.RegisterGasLimit, out string? error))
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid block. Result of {payloadStr}. Error: {error}");
            return BlockValidationResult.Invalid(error ?? "Block validation failed");
        }

        if (block is not null && !ValidateBlobsBundle(block.Transactions, blobsBundle, out string? blobsError))
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid blobs bundle. Result of {payloadStr}. Error: {blobsError}");
            return BlockValidationResult.Invalid(blobsError ?? "Blobs bundle validation failed");
        }


        return BlockValidationResult.Valid();
    }
}
