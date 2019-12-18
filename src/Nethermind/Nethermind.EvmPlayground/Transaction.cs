//  Copyright (c) 2018 Demerzel Solutions Limited
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

using Newtonsoft.Json;

namespace Nethermind.EvmPlayground
{
    public class Transaction
    {
        public Transaction(byte[] sender, byte[] data)
        {
            From = sender.ToHexString(true);
            Data = data.ToHexString(true);
        }
       
        [JsonProperty("from", Order = 0)]
        public string From { get; }

        [JsonProperty("gas", Order = 1)]
        public string Gas { get; } = "0xF4240";

        [JsonProperty("gasPrice", Order = 2)]
        public string GasPrice { get; } = "0x4A817C800";

        [JsonProperty("to", Order = 3)]
        public string To { get; }

        [JsonProperty("data", Order = 4)]
        public string Data { get; }
    }
}