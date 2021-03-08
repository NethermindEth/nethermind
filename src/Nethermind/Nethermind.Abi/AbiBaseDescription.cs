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

using System;
using System.Linq;
using Nethermind.Core.Crypto;

namespace Nethermind.Abi
{
    public abstract class AbiBaseDescription
    {
        public AbiDescriptionType Type { get; set; } = AbiDescriptionType.Function;
        public string Name { get; set; } = string.Empty;
    }
    
    public abstract class AbiBaseDescription<T> : AbiBaseDescription where T : AbiParameter
    {
        private AbiSignature? _callSignature;
        
        public T[] Inputs { get; set; } = Array.Empty<T>();
        
        public AbiEncodingInfo GetCallInfo(AbiEncodingStyle encodingStyle = AbiEncodingStyle.IncludeSignature) => 
            new(encodingStyle, _callSignature ??= new AbiSignature(Name, Inputs.Select(i => i.Type).ToArray()));

        public Keccak GetHash() => GetCallInfo().Signature.Hash;

    }
}
