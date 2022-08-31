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
using System.Linq;
using System.Threading.Tasks;
using DotNetty.Common.Utilities;
using MathNet.Numerics.Random;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Data.V1;

namespace Nethermind.Merge.Plugin.Handlers.V1
{
    /// <summary>
    /// engine_getPayloadV1
    ///
    /// Given a 8 byte payload_id, it returns the most recent version of an execution payload that is available by the time of the call or responds with an error.
    ///
    /// <see cref="https://github.com/ethereum/execution-apis/blob/main/src/engine/specification.md#engine_getpayloadv1"/>
    /// </summary>
    /// <remarks>
    /// This call must be responded immediately. An exception would be the case when no version of the payload is ready yet and in this case there might be a slight delay before the response is done.
    /// Execution client should create a payload with empty transaction set to be able to respond as soon as possible.
    /// If there were no prior engine_preparePayload call with the corresponding payload_id or the process of building a payload has been cancelled due to the timeout then execution client must respond with error message.
    /// Execution client may stop the building process with the corresponding payload_id value after serving this call.
    /// </remarks>
    public class GetPayloadV1Handler : IAsyncHandler<byte[], ExecutionPayloadV1?>
    {
        private readonly IPayloadPreparationService _payloadPreparationService;
        private readonly ILogger _logger;

        public GetPayloadV1Handler(IPayloadPreparationService payloadPreparationService, ILogManager logManager)
        {
            _payloadPreparationService = payloadPreparationService;
            _logger = logManager.GetClassLogger();
        }

        public async Task<ResultWrapper<ExecutionPayloadV1?>> HandleAsync(byte[] payloadId)
        {
            string payloadStr = payloadId.ToHexString(true);
            Block? block = await _payloadPreparationService.GetPayload(payloadStr);

            // corrupting block for testing purposes
            CorruptBlock(ref block);

            if (block == null)
            {
                // The call MUST return -38001: Unknown payload error if the build process identified by the payloadId does not exist.
                if (_logger.IsWarn) _logger.Warn($"Block production for payload with id={payloadId.ToHexString()} failed - unknown payload.");
                return ResultWrapper<ExecutionPayloadV1?>.Fail("unknown payload", MergeErrorCodes.UnknownPayload);
            }

            if (_logger.IsInfo) _logger.Info($"GetPayloadV1 result: {block.Header.ToString(BlockHeader.Format.Full)}.");

            return ResultWrapper<ExecutionPayloadV1?>.Success(new ExecutionPayloadV1(block));
        }


        [ThreadStatic] private readonly System.Random _random = new Random();
        private void CorruptBlock(ref Block? block)
        {
            if (block is not null)
            {
                block.Header.GasUsed = _random.NextLong();
                block.Header.Author = _random.NextBoolean() ? block.Header.Beneficiary : block.Header.Author;
                block.Header.Beneficiary = _random.NextBoolean() ? block.Header.Author : block.Header.Beneficiary;
                block.Header.TotalDifficulty = UInt256.Parse(
                    Enumerable.Range(0, 78)
                        .Select(_ => _random.Next(0, 10))
                        .Aggregate(String.Empty, (acc, val) => $"{acc}{val}")
                        );
                block.Header.StateRoot = block.CalculateHash();
                block.Header.Hash = block.Header.CalculateHash();
            }
        }
    }
}
