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

using Newtonsoft.Json;

namespace Nethermind.KeyStore
{
    public class Crypto
    {
        [JsonProperty(PropertyName = "ciphertext", Order = 0)]
        public string CipherText { get; set; }
        
        [JsonProperty(PropertyName = "cipherparams", Order = 1)]
        public CipherParams CipherParams { get; set; }
        
        [JsonProperty(PropertyName = "cipher", Order = 2)]
        public string Cipher { get; set; }
        
        [JsonProperty(PropertyName = "kdf", Order = 3)]
        public string KDF { get; set; }
        
        [JsonProperty(PropertyName = "kdfparams", Order = 4)]
        public KdfParams KDFParams { get; set; }
        
        [JsonProperty(PropertyName = "mac", Order = 5)]
        public string MAC { get; set; }
    }
}
