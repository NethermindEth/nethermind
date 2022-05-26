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
    public class PayloadStatusV1
    {
        public static PayloadStatusV1 InvalidBlockHash = new() { Status = PayloadStatus.InvalidBlockHash };

        public static PayloadStatusV1 Syncing = new() { Status = PayloadStatus.Syncing };
        
        public static PayloadStatusV1 Accepted = new() { Status = PayloadStatus.Accepted };

        public string Status { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public Keccak? LatestValidHash { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public string? ValidationError { get; set; }
    }
}
