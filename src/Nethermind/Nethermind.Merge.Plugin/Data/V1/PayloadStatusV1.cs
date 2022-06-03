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
using Newtonsoft.Json;

namespace Nethermind.Merge.Plugin.Data.V1
{
    /// <summary>
    /// Result of engine_newPayloadV1 call.
    /// 
    /// <seealso cref="https://github.com/ethereum/execution-apis/blob/main/src/engine/specification.md#PayloadStatusV1"/>
    /// </summary>
    public class PayloadStatusV1
    {
        public static readonly PayloadStatusV1 InvalidBlockHash = new() { Status = PayloadStatus.InvalidBlockHash };

        public static readonly PayloadStatusV1 Syncing = new() { Status = PayloadStatus.Syncing };
        
        public static readonly PayloadStatusV1 Accepted = new() { Status = PayloadStatus.Accepted };

        /// <summary>
        /// One of <see cref="PayloadStatus"/>.
        /// </summary>
        public string Status { get; set; }

        /// <summary>
        /// Hash of the most recent valid block in the branch defined by payload and its ancestors.
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public Keccak? LatestValidHash { get; set; }
        
        /// <summary>
        /// Message providing additional details on the validation error if the payload is classified as <see cref="PayloadStatus.Invalid"/> or <see cref="PayloadStatus.InvalidBlockHash"/>. 
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public string? ValidationError { get; set; }
    }
}
