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
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Newtonsoft.Json;

namespace Nethermind.Merge.Plugin.Data.V1
{
    /// <summary>
    /// Result of engine_forkChoiceUpdate call.
    ///
    /// <seealso cref="https://github.com/ethereum/execution-apis/blob/main/src/engine/specification.md#response-1"/>
    /// </summary>
    public class ForkchoiceUpdatedV1Result
    {
        public static readonly ResultWrapper<ForkchoiceUpdatedV1Result> Syncing = ResultWrapper<ForkchoiceUpdatedV1Result>.Success(new ForkchoiceUpdatedV1Result { PayloadId = null, PayloadStatus = PayloadStatusV1.Syncing });

        public static ResultWrapper<ForkchoiceUpdatedV1Result> Valid(string? payloadId, Keccak? latestValidHash) => 
            ResultWrapper<ForkchoiceUpdatedV1Result>.Success(new ForkchoiceUpdatedV1Result { PayloadId = payloadId, PayloadStatus = new PayloadStatusV1() { Status = Data.V1.PayloadStatus.Valid, LatestValidHash = latestValidHash } });

        public static ResultWrapper<ForkchoiceUpdatedV1Result> Invalid(Keccak? latestValidHash, string? validationError = null) =>
            ResultWrapper<ForkchoiceUpdatedV1Result>.Success(new ForkchoiceUpdatedV1Result
            {
                PayloadStatus = new PayloadStatusV1 { Status = Data.V1.PayloadStatus.Invalid, LatestValidHash = latestValidHash, ValidationError = validationError }
            });

        public static ResultWrapper<ForkchoiceUpdatedV1Result> Error(string message, int errorCode) => ResultWrapper<ForkchoiceUpdatedV1Result>.Fail(message, errorCode);

        /// <summary>
        /// Status
        /// </summary>
        public PayloadStatusV1 PayloadStatus { get; set; } = PayloadStatusV1.Syncing;

        /// <summary>
        /// Identifier of the payload build process or null if there is none.
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public string? PayloadId { get; set; }

        public static implicit operator Task<ForkchoiceUpdatedV1Result>(ForkchoiceUpdatedV1Result result) => Task.FromResult(result);
    }
}
