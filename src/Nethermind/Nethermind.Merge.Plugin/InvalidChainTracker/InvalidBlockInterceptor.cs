// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Logging;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Merge.Plugin.InvalidChainTracker;

public class InvalidBlockInterceptor : IBlockValidator
{
    private readonly IBlockValidator _baseValidator;
    private readonly IInvalidChainTracker _invalidChainTracker;
    private readonly ILogger _logger;

    public InvalidBlockInterceptor(
        IBlockValidator headerValidator,
        IInvalidChainTracker invalidChainTracker,
        ILogManager logManager)
    {
        _baseValidator = headerValidator;
        _invalidChainTracker = invalidChainTracker;
        _logger = logManager.GetClassLogger(typeof(InvalidBlockInterceptor));
    }

    public bool ValidateOrphanedBlock(Block block, [NotNullWhen(false)] out string? error) => _baseValidator.ValidateOrphanedBlock(block, out error);

    public bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle = false)
    {
        return Validate(header, parent, isUncle, out _);
    }
    public bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle, [NotNullWhen(false)] out string? error)
    {
        bool result = _baseValidator.Validate(header, parent, isUncle, out error);
        if (!result)
        {
            if (_logger.IsTrace) _logger.Trace($"Intercepted a bad header {header}");
            if (ShouldNotTrackInvalidation(header))
            {
                if (_logger.IsDebug) _logger.Debug($"Header invalidation should not be tracked");
                return result;
            }
            _invalidChainTracker.OnInvalidBlock(header.Hash!, header.ParentHash);
        }
        _invalidChainTracker.SetChildParent(header.Hash!, header.ParentHash!);
        return result;
    }

    public bool Validate(BlockHeader header, bool isUncle = false)
    {
        return Validate(header, isUncle, out _);
    }

    public bool Validate(BlockHeader header, bool isUncle, [NotNullWhen(false)] out string? error)
    {
        bool result = _baseValidator.Validate(header, isUncle, out error);
        if (!result)
        {
            if (_logger.IsTrace) _logger.Trace($"Intercepted a bad header {header}");
            if (ShouldNotTrackInvalidation(header))
            {
                if (_logger.IsDebug) _logger.Debug($"Header invalidation should not be tracked");
                return result;
            }
            _invalidChainTracker.OnInvalidBlock(header.Hash!, header.ParentHash);
        }
        _invalidChainTracker.SetChildParent(header.Hash!, header.ParentHash!);
        return result;
    }

    public bool ValidateSuggestedBlock(Block block)
    {
        return ValidateSuggestedBlock(block, out _);
    }
    public bool ValidateSuggestedBlock(Block block, [NotNullWhen(false)] out string? error)
    {
        bool result = _baseValidator.ValidateSuggestedBlock(block, out error);
        if (!result)
        {
            if (_logger.IsTrace) _logger.Trace($"Intercepted a bad block {block}");
            if (ShouldNotTrackInvalidation(block))
            {
                if (_logger.IsDebug) _logger.Debug($"Block invalidation should not be tracked");
                return result;
            }
            _invalidChainTracker.OnInvalidBlock(block.Hash!, block.ParentHash);
        }
        _invalidChainTracker.SetChildParent(block.Hash!, block.ParentHash!);
        return result;
    }

    public bool ValidateProcessedBlock(Block block, TxReceipt[] receipts, Block suggestedBlock)
    {
        return ValidateProcessedBlock(block, receipts, suggestedBlock, out _);
    }
    public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock, [NotNullWhen(false)] out string? error)
    {
        bool result = _baseValidator.ValidateProcessedBlock(processedBlock, receipts, suggestedBlock, out error);
        if (!result)
        {
            if (_logger.IsTrace) _logger.Trace($"Intercepted a bad block {processedBlock}");
            if (ShouldNotTrackInvalidation(processedBlock))
            {
                if (_logger.IsDebug) _logger.Debug($"Block invalidation should not be tracked");
                return result;
            }
            _invalidChainTracker.OnInvalidBlock(suggestedBlock.Hash!, suggestedBlock.ParentHash);
        }
        _invalidChainTracker.SetChildParent(suggestedBlock.Hash!, suggestedBlock.ParentHash!);

        return result;
    }

    private static bool ShouldNotTrackInvalidation(BlockHeader header)
    {
        return !HeaderValidator.ValidateHash(header);
    }

    public bool ValidateWithdrawals(Block block, out string? error)
    {
        bool result = _baseValidator.ValidateWithdrawals(block, out error);

        if (!result)
        {
            if (_logger.IsTrace) _logger.Trace($"Intercepted a bad block {block}");

            if (ShouldNotTrackInvalidation(block.Header))
            {
                if (_logger.IsDebug) _logger.Debug($"Block invalidation should not be tracked");

                return false;
            }

            _invalidChainTracker.OnInvalidBlock(block.Hash!, block.ParentHash);
        }

        _invalidChainTracker.SetChildParent(block.Hash!, block.ParentHash!);

        return result;
    }

    private static bool ShouldNotTrackInvalidation(Block block)
    {
        if (ShouldNotTrackInvalidation(block.Header))
            return true;

        // Body does not match header, but it does not mean the hash that the header point to is invalid.
        if (!BlockValidator.ValidateTxRootMatchesTxs(block, out _))
            return true;

        if (!BlockValidator.ValidateUnclesHashMatches(block, out _))
            return true;

        return !BlockValidator.ValidateWithdrawalsHashMatches(block, out _);
    }

}
