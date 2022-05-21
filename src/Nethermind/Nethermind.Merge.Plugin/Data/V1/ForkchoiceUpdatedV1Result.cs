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

using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Newtonsoft.Json;

namespace Nethermind.Merge.Plugin.Data.V1
{
    public class ForkchoiceUpdatedV1Result
    {
        private static readonly ForkchoiceUpdatedV1Result _syncing = new()
        {
            PayloadId = null, PayloadStatus = PayloadStatusV1.Syncing
        };

        public static readonly ResultWrapper<ForkchoiceUpdatedV1Result> Syncing =
            ResultWrapper<ForkchoiceUpdatedV1Result>.Success(_syncing);

        public static ResultWrapper<ForkchoiceUpdatedV1Result> Valid(string? payloadId, Keccak? latestValidHash)
        {
            return ResultWrapper<ForkchoiceUpdatedV1Result>.Success(new ForkchoiceUpdatedV1Result()
            {
                PayloadId = payloadId,
                PayloadStatus = new PayloadStatusV1()
                {
                    Status = Data.V1.PayloadStatus.Valid, LatestValidHash = latestValidHash
                }
            });
        }
        
        public static ResultWrapper<ForkchoiceUpdatedV1Result> Invalid(Keccak? latestValidHash, string? validationError = null)
        {
            return ResultWrapper<ForkchoiceUpdatedV1Result>.Success(new ForkchoiceUpdatedV1Result()
            {
                PayloadStatus = new PayloadStatusV1()
                {
                    Status = Data.V1.PayloadStatus.Invalid, LatestValidHash = latestValidHash, ValidationError = validationError
                }
            });
        }
        
        public static ResultWrapper<ForkchoiceUpdatedV1Result> Error(string message, int errorCode)
        {
            return ResultWrapper<ForkchoiceUpdatedV1Result>.Fail(message, errorCode);
        }

        public PayloadStatusV1 PayloadStatus { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public string? PayloadId { get; set; }
    }
}
