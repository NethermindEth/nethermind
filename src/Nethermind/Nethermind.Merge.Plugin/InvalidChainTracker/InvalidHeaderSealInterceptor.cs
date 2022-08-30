//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin.InvalidChainTracker;

public class InvalidHeaderSealInterceptor: ISealValidator
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

    public bool ValidateParams(BlockHeader parent, BlockHeader header)
    {
        bool result = _baseValidator.ValidateParams(parent, header);
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
