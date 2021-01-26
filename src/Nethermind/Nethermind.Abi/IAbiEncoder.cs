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

namespace Nethermind.Abi
{
    public interface IAbiEncoder
    {
        /// <summary>
        /// Encodes data in accordance to the Solidity ABI encoding.
        /// </summary>
        /// <param name="encodingStyle">One of the ABI styles.</param>
        /// <param name="signature">The signature of the Solidity method.</param>
        /// <param name="arguments">Arguments of the Solidity method.</param>
        /// <returns>ABI encoded data.</returns>
        byte[] Encode(AbiEncodingStyle encodingStyle, AbiSignature signature, params object[] arguments);
        
        /// <summary>
        /// Decodes ABI encoded data into a set of objects.
        /// </summary>
        /// <param name="encodingStyle">One of the ABI styles.</param>
        /// <param name="signature">Signature of the Solidity method for which the arguments were passed.</param>
        /// <param name="data">ABI encoded data.</param>
        /// <returns></returns>
        object[] Decode(AbiEncodingStyle encodingStyle, AbiSignature signature, byte[] data);
    }
}
