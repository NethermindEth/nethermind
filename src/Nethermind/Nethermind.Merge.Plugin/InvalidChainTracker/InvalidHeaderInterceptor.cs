// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Logging;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Merge.Plugin.InvalidChainTracker;

public class InvalidHeaderInterceptor : IHeaderValidator
{
    private readonly IHeaderValidator _baseValidator;
    private readonly IInvalidChainTracker _invalidChainTracker;
    private readonly ILogger _logger;

    public InvalidHeaderInterceptor(
        IHeaderValidator headerValidator,
        IInvalidChainTracker invalidChainTracker,
        ILogManager logManager)
    {
        _baseValidator = headerValidator;
        _invalidChainTracker = invalidChainTracker;
        _logger = logManager.GetClassLogger(typeof(InvalidHeaderInterceptor));
    }

    public bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle = false)
    {
        return Validate(header, parent, isUncle, out _);
    }

    public bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle, [NotNullWhen(false)] out string? error)
    {
        bool result = _baseValidator.Validate(header, parent, isUncle, out error);
        if (!result)
        {
            if (_logger.IsDebug) _logger.Debug($"Intercepted a bad header {header}");
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
            if (_logger.IsDebug) _logger.Debug($"Intercepted a bad header {header}");
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

    private static bool ShouldNotTrackInvalidation(BlockHeader header)
    {
        return !HeaderValidator.ValidateHash(header);
    }

}
