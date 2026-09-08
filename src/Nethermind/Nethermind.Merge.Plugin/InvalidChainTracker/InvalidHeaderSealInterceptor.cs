// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
}
