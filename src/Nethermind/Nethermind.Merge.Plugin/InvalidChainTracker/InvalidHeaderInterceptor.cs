// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Logging;

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
        bool result = _baseValidator.Validate(header, parent, isUncle);
        if (!result)
        {
            if (_logger.IsDebug) _logger.Debug($"Intercepted a bad header {header}");
            if (ShouldNotTrackInvalidation(header))
            {
                if (_logger.IsDebug) _logger.Debug($"Header invalidation should not be tracked");
                return false;
            }
            _invalidChainTracker.OnInvalidBlock(header.Hash!, header.ParentHash);
        }
        _invalidChainTracker.SetChildParent(header.Hash!, header.ParentHash!);
        return result;
    }

    public bool Validate(BlockHeader header, bool isUncle = false)
    {
        bool result = _baseValidator.Validate(header, isUncle);
        if (!result)
        {
            if (_logger.IsDebug) _logger.Debug($"Intercepted a bad header {header}");
            if (ShouldNotTrackInvalidation(header))
            {
                if (_logger.IsDebug) _logger.Debug($"Header invalidation should not be tracked");
                return false;
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
