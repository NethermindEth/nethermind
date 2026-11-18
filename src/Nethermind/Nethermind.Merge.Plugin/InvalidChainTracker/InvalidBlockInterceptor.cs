// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Logging;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Merge.Plugin.InvalidChainTracker;

public class InvalidBlockInterceptor(
    IBlockValidator blockValidator,
    IInvalidChainTracker invalidChainTracker,
    ILogManager logManager)
    : IBlockValidator
{
    private readonly ILogger _logger = logManager.GetClassLogger<InvalidBlockInterceptor>();

    public bool ValidateOrphanedBlock(Block block, [NotNullWhen(false)] out string? error) => blockValidator.ValidateOrphanedBlock(block, out error);

    public bool Validate(BlockHeader header, BlockHeader parent, bool isUncle, [NotNullWhen(false)] out string? error) =>
        TrackValidationResult(header, blockValidator.Validate(header, parent, isUncle, out error));

    public bool Validate(BlockHeader header, BlockHeader parent, bool isUncle, [NotNullWhen(false)] out string? error, bool validateHash) =>
        TrackValidationResult(header, blockValidator.Validate(header, parent, isUncle, out error, validateHash));

    private bool TrackValidationResult(BlockHeader header, bool result)
    {
        if (UntrackedValidation.IsActive) return result;
        if (!result)
        {
            if (_logger.IsTrace) _logger.Trace($"Intercepted a bad header {header}");
            if (ShouldNotTrackInvalidation(header))
            {
                if (_logger.IsDebug) _logger.Debug($"Header invalidation should not be tracked");
                return result;
            }
            invalidChainTracker.OnInvalidBlock(header.Hash!, header.ParentHash);
        }
        invalidChainTracker.SetChildParent(header.Hash!, header.ParentHash!);
        return result;
    }

    public bool ValidateOrphaned(BlockHeader header, [NotNullWhen(false)] out string? error) =>
        blockValidator.ValidateOrphaned(header, out error);

    public bool ValidateSuggestedBlock(Block block, BlockHeader parent, [NotNullWhen(false)] out string? error, bool validateHashes = true)
    {
        bool result = blockValidator.ValidateSuggestedBlock(block, parent, out error, validateHashes);
        if (UntrackedValidation.IsActive) return result;
        if (!result)
        {
            if (_logger.IsTrace) _logger.Trace($"Intercepted a bad block {block}");
            if (ShouldNotTrackInvalidation(block))
            {
                if (_logger.IsDebug) _logger.Debug($"Block invalidation should not be tracked");
                return result;
            }
            invalidChainTracker.OnInvalidBlock(block.Hash!, block.ParentHash);
        }
        invalidChainTracker.SetChildParent(block.Hash!, block.ParentHash!);
        return result;
    }

    public bool ValidateProcessedBlock(Block block, TxReceipt[] receipts, Block suggestedBlock) => ValidateProcessedBlock(block, receipts, suggestedBlock, out _);

    /// <remarks>
    /// Forwards only. Every block processing environment shares this validator, and an execution result depends on the
    /// state it ran on, which only the node's own import is sure of. The import reports its execution failures itself:
    /// the processing queue raises <c>InvalidBlock</c>, which <c>InvalidChainTracker.SetupBlockchainProcessorInterceptor</c>
    /// subscribes to, and newPayload records the INVALID it answers. Guarded by
    /// <c>EngineModuleTests.Queue_imported_post_execution_failure_marks_the_block_and_descendants_invalid</c>.
    /// </remarks>
    public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock, [NotNullWhen(false)] out string? error) =>
        blockValidator.ValidateProcessedBlock(processedBlock, receipts, suggestedBlock, out error);

    private static bool ShouldNotTrackInvalidation(BlockHeader header) => !HeaderValidator.ValidateHash(header);

    public bool ValidateWithdrawals(Block block, out string? error)
    {
        bool result = blockValidator.ValidateWithdrawals(block, out error);
        if (UntrackedValidation.IsActive) return result;

        if (!result)
        {
            if (_logger.IsTrace) _logger.Trace($"Intercepted a bad block {block}");

            if (ShouldNotTrackInvalidation(block.Header))
            {
                if (_logger.IsDebug) _logger.Debug($"Block invalidation should not be tracked");

                return false;
            }

            invalidChainTracker.OnInvalidBlock(block.Hash!, block.ParentHash);
        }

        invalidChainTracker.SetChildParent(block.Hash!, block.ParentHash!);

        return result;
    }

    public bool ValidateBodyAgainstHeader(BlockHeader header, BlockBody toBeValidated, [NotNullWhen(false)] out string? error) =>
        blockValidator.ValidateBodyAgainstHeader(header, toBeValidated, out error);

    private bool ShouldNotTrackInvalidation(Block block) =>
        ShouldNotTrackInvalidation(block.Header) ||
        // Body does not match header, but it does not mean the hash that the header point to is invalid.
        !blockValidator.ValidateBodyAgainstHeader(block.Header, block.Body, out _);
}
