// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading.Tasks;
using Nethermind.BlockValidation.Data;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.BlockValidation.Handlers;

public class ValidateSubmissionHandler
{
    private readonly ILogger _logger;

    public ValidateSubmissionHandler(ILogManager logManager)
    {
        _logger = logManager.GetClassLogger();
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

    private bool ValidatePayload(Block block, Address feeRecipient, UInt256 expectedProfit, ulong registerGasLimit, out string? error)
    {
        // TODO: Implement this method
        error = null;
        return true;
    }

    private bool ValidateBlock(Block block, BidTrace message, ulong registerGasLimit, out string? error)
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
