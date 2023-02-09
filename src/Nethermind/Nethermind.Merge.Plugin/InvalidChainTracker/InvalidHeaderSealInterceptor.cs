// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin.InvalidChainTracker;

public class InvalidHeaderSealInterceptor : ISealValidator
{
    private readonly ISealValidator _baseValidator;
    private readonly IInvalidChainTracker _invalidChainTracker;
    private readonly ILogger _logger;

    public InvalidHeaderSealInterceptor(ISealValidator baseValidator, IInvalidChainTracker invalidChainTracker, ILogManager logManager)
    {
        _baseValidator = baseValidator;
        _invalidChainTracker = invalidChainTracker;
        _logger = logManager.GetClassLogger(typeof(InvalidHeaderInterceptor));
    }

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
}
