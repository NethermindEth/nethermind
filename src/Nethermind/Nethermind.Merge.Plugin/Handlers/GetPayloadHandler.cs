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

using System;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers
{
    public class GetPayloadHandler : IHandler<ulong, BlockRequestResult?>
    {
        private readonly PayloadStorage _payloadStorage;
        private readonly ILogger _logger;

        public GetPayloadHandler(PayloadStorage payloadStorage, ILogManager logManager)
        {
            _payloadStorage = payloadStorage;
            _logger = logManager.GetClassLogger();
        }

        public ResultWrapper<BlockRequestResult?> Handle(ulong payloadId)
        {
            Tuple<Block?, Keccak>? blockAndRandom = _payloadStorage.GetPayload(payloadId);
            
            if (blockAndRandom?.Item1 == null)
            {
                if (_logger.IsWarn) _logger.Warn($"Block production failed");
                return ResultWrapper<BlockRequestResult?>.Fail(
                    $"Execution payload requested with id={payloadId} cannot be found.",
                    MergeErrorCodes.UnavailablePayload);
            }
            else
            {
                return ResultWrapper<BlockRequestResult?>.Success(new BlockRequestResult(blockAndRandom.Item1,
                    blockAndRandom.Item2));
            }
        }
    }
}
