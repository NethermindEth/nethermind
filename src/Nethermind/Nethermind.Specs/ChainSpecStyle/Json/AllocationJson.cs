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

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Specs.ChainSpecStyle.Json
{
    internal class AllocationJson
    {
        public object BuiltIn { get; set; }
        
        public UInt256 Balance { get; set; }
        
        public byte[] Code { get; set; }
        
        public byte[] Constructor { get; set; }
        public Dictionary<string, string> Storage { get; set; }

        public Dictionary<UInt256, byte[]> GetConvertedStorage()
        {
            if(Storage == null)
                return null;
            
            return Storage.ToDictionary(s => Bytes.FromHexString(s.Key).ToUInt256(), s => Bytes.FromHexString(s.Value));
        }
    }
}