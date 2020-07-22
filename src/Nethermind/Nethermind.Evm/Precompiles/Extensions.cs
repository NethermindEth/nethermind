//  Copyright (c) 2020s Demerzel Solutions Limited
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
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Evm.Precompiles
{
    public static class Extensions
    {
        public static void PrepareEthInput(this byte[] inputData, Span<byte> inputDataSpan)
        {
            inputData ??= Array.Empty<byte>();
            inputData.AsSpan(0, Math.Min(inputDataSpan.Length, inputData.Length))
                .CopyTo(inputDataSpan.Slice(0, Math.Min(inputDataSpan.Length, inputData.Length)));
        }
        
        public static UInt256 ReadMclScalar(in Span<byte> inputDataSpan, in int offset)
        {
            Span<byte> s = inputDataSpan.Slice(offset, 32);
            UInt256.CreateFromBigEndian(out UInt256 scalar, s);
            return scalar;
        }
    }
}