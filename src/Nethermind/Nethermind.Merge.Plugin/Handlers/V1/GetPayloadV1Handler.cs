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

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers.V1
{
    /// <summary>
    /// https://hackmd.io/@n0ble/kintsugi-spec
    /// engine_getPayloadV1. Given a 8 byte payload_id, it returns the most recent version of an execution payload
    /// that is available by the time of the call or responds with an error.
    /// This call must be responded immediately. An exception would be the case when no version of the payload
    /// is ready yet and in this case there might be a slight delay before the response is done.
    /// Execution client should create a payload with empty transaction set to be able to respond as soon as possible.
    /// If there were no prior engine_preparePayload call with the corresponding payload_id or the process of building
    /// a payload has been cancelled due to the timeout then execution client must respond with error message.
    /// Execution client may stop the building process with the corresponding payload_id value after serving this call.
    /// </summary>
    public class GetPayloadV1Handler: IAsyncHandler<byte[], BlockRequestResult?>
    {
        private readonly PayloadService _payloadService;
        private readonly ILogger _logger;

        public GetPayloadV1Handler(PayloadService payloadService, ILogManager logManager)
        {
            _payloadService = payloadService;
            _logger = logManager.GetClassLogger();
        }

        public async Task<ResultWrapper<BlockRequestResult?>> HandleAsync(byte[] payloadId)
        {
            Task<Block?>? blockProductionTask = _payloadService.GetPayload(payloadId);
            
            if (blockProductionTask == null)
            {
                if (_logger.IsWarn) _logger.Warn($"No payload with id={payloadId} found");
                return ResultWrapper<BlockRequestResult?>.Fail(
                    "unknown payload",
                    MergeErrorCodes.UnavailablePayloadV1);
            }
            
            Block? block = await blockProductionTask;

            if (block == null)
            {
                if (_logger.IsWarn) _logger.Warn($"Block production for payload with id={payloadId} failed");
                return ResultWrapper<BlockRequestResult?>.Fail(
                    "unknown payload",
                    MergeErrorCodes.UnavailablePayloadV1);
            }

            if (_logger.IsInfo)
            {
                _logger.Info(block.Header.ToString(BlockHeader.Format.Full));
            }

            BlockRequestResult result = new(block);
            return ResultWrapper<BlockRequestResult?>.Success(result);
        }
    }
}
