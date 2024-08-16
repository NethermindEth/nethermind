// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.BlockValidation.Data;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
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

        if(message.GasLimit != block.GasLimit)
        {
            error = $"Gas limit mismatch. Expected {message.GasLimit} but got {block.GasLimit}";
            return false;
        }

        if(message.GasUsed != block.GasUsed)
        {
            error = $"Gas used mismatch. Expected {message.GasUsed} but got {block.GasUsed}";
            return false;
        }

        return true;
    }
 
    public Task<ResultWrapper<BlockValidationResult>> ValidateSubmission(BuilderBlockValidationRequest request)
    {
        ExecutionPayload payload = request.BlockRequest.ExecutionPayload;
        BlobsBundleV1 blobsBundle = request.BlockRequest.BlobsBundle;

        string payloadStr = $"BuilderBlock: {payload}";

        _logger.Info($"blobs bundle blobs {blobsBundle.Blobs.Length} commits {blobsBundle.Commitments.Length} proofs {blobsBundle.Proofs.Length}");
        
        if(!payload.TryGetBlock(out Block? block))
        {
            if(_logger.IsWarn) _logger.Warn($"Invalid block. Result of {payloadStr}.");
            return BlockValidationResult.Invalid($"Block {payload} coud not be parsed as a block");
        }



        return BlockValidationResult.Valid();
    }
}
