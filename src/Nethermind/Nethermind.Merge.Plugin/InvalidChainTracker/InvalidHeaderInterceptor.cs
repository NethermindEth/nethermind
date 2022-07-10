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

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin.InvalidChainTracker;

public class InvalidHeaderInterceptor: IHeaderValidator
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
        _invalidChainTracker.SetChildParent(header.Hash!, header.ParentHash!);
        bool result = _baseValidator.Validate(header, parent, isUncle);
        if (!result)
        {
            if (_logger.IsTrace) _logger.Trace($"Intercepted a bad header {header}");
            _invalidChainTracker.OnInvalidBlock(header.Hash!, header.ParentHash);
        }
        return result;
    }

    public bool Validate(BlockHeader header, bool isUncle = false)
    {
        _invalidChainTracker.SetChildParent(header.Hash!, header.ParentHash!);
        bool result = _baseValidator.Validate(header, isUncle);
        if (!result)
        {
            if (_logger.IsTrace) _logger.Trace($"Intercepted a bad header {header}");
            _invalidChainTracker.OnInvalidBlock(header.Hash!, header.ParentHash);
        }
        return result;
    }
}
