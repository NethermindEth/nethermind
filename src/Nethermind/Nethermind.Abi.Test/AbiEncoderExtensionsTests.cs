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

using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Abi.Test
{
    public class AbiEncoderExtensionsTests
    {
        [Test]
        public void Encode_should_be_called()
        {
            var abi = Substitute.For<IAbiEncoder>();
            var parameters = new object[] {"p1"};
            var abiSignature = new AbiSignature("test", AbiType.String);
            const AbiEncodingStyle abiEncodingStyle = AbiEncodingStyle.Packed;
            
            abi.Encode(new AbiEncodingInfo(abiEncodingStyle, abiSignature), parameters);
            abi.Received().Encode(abiEncodingStyle, abiSignature, parameters);
        }
        
        [Test]
        public void Decode_should_be_called()
        {
            var abi = Substitute.For<IAbiEncoder>();
            var data = new byte[] {100, 200};
            var abiSignature = new AbiSignature("test", AbiType.String);
            const AbiEncodingStyle abiEncodingStyle = AbiEncodingStyle.Packed;
            
            abi.Decode(new AbiEncodingInfo(abiEncodingStyle, abiSignature), data);
            abi.Received().Decode(abiEncodingStyle, abiSignature, data);
        }
    }
}
