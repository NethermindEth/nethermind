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

    public bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle = false) => Validate(header, parent, isUncle, out _);

    public bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle, [NotNullWhen(false)] out string? error)
    {
        bool result = blockValidator.Validate(header, parent, isUncle, out error);
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

    public bool Validate(BlockHeader header, bool isUncle = false) => Validate(header, isUncle, out _);

    public bool Validate(BlockHeader header, bool isUncle, [NotNullWhen(false)] out string? error)
    {
        bool result = blockValidator.Validate(header, isUncle, out error);
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

    public bool ValidateSuggestedBlock(Block block, [NotNullWhen(false)] out string? error, bool validateHashes = true)
    {
        bool result = blockValidator.ValidateSuggestedBlock(block, out error, validateHashes);
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

    public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock, [NotNullWhen(false)] out string? error)
    {
        bool result = blockValidator.ValidateProcessedBlock(processedBlock, receipts, suggestedBlock, out error);
        if (!result)
        {
            if (_logger.IsTrace) _logger.Trace($"Intercepted a bad block {processedBlock}");
            if (ShouldNotTrackInvalidation(processedBlock))
            {
                if (_logger.IsDebug) _logger.Debug($"Block invalidation should not be tracked");
                return result;
            }
            invalidChainTracker.OnInvalidBlock(suggestedBlock.Hash!, suggestedBlock.ParentHash);
        }
        invalidChainTracker.SetChildParent(suggestedBlock.Hash!, suggestedBlock.ParentHash!);

        return result;
    }

    private static bool ShouldNotTrackInvalidation(BlockHeader header) => !HeaderValidator.ValidateHash(header);

    public bool ValidateWithdrawals(Block block, out string? error)
    {
        bool result = blockValidator.ValidateWithdrawals(block, out error);

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

    public bool ValidateBodyAgainstHeader(BlockHeader header, BlockBody toBeValidated, [NotNullWhen(false)] out string? errorMessage) =>
        blockValidator.ValidateBodyAgainstHeader(header, toBeValidated, out errorMessage);

    private bool ShouldNotTrackInvalidation(Block block) =>
        ShouldNotTrackInvalidation(block.Header) ||
        // Body does not match header, but it does not mean the hash that the header point to is invalid.
        !blockValidator.ValidateBodyAgainstHeader(block.Header, block.Body, out _);
}
