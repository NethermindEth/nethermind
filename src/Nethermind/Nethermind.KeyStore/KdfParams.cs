﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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

namespace Nethermind.KeyStore
{
    public class KdfParams
    {
        [JsonProperty(PropertyName = "dklen", Order = 0)]
        public int DkLen { get; set; }
        
        [JsonProperty(PropertyName = "salt", Order = 1)]
        public string Salt { get; set; }
        
        [JsonProperty(PropertyName = "n", Order = 2)]
        public int? N { get; set; }
        
        [JsonProperty(PropertyName = "p", Order = 4)]
        public int? P { get; set; }
        
        [JsonProperty(PropertyName = "r", Order = 3)]
        public int? R { get; set; }

        [JsonProperty(PropertyName = "c")]
        public int? C { get; set; }
        
        [JsonProperty(PropertyName = "prf")]
        public string Prf { get; set; }
    }
}