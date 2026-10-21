// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin.InvalidChainTracker;

public class InvalidHeaderSealInterceptor(ISealValidator baseValidator, IInvalidChainTracker invalidChainTracker, ILogManager logManager) : ISealValidator
{
    private readonly ISealValidator _baseValidator = baseValidator;
    private readonly IInvalidChainTracker _invalidChainTracker = invalidChainTracker;
    private readonly ILogger _logger = logManager.GetClassLogger<InvalidHeaderInterceptor>();

    public bool ValidateParams(BlockHeader parent, BlockHeader header, bool isUncle = false)
    {
        bool result = _baseValidator.ValidateParams(parent, header, isUncle);
        if (!result)
        {
            if (_logger.IsDebug) _logger.Debug($"Intercepted a header with bad seal param {header}");
            _invalidChainTracker.OnInvalidBlock(header.Hash!, header.ParentHash);
        }
        return result;
    }

    public bool ValidateSeal(BlockHeader header, bool force)
    {
        bool result = _baseValidator.ValidateSeal(header, force);
        if (!result)
        {
            if (_logger.IsDebug) _logger.Debug($"Intercepted a header with bad seal {header}");
            _invalidChainTracker.OnInvalidBlock(header.Hash!, header.ParentHash);
        }
        return result;
    }

    // Must forward: the interface default is a no-op, so a decorator that omits this silently swallows the
    // hint and the wrapped validator never prepares its cache.
    public void HintValidationRange(Guid guid, ulong start, ulong end) =>
        _baseValidator.HintValidationRange(guid, start, end);
}
