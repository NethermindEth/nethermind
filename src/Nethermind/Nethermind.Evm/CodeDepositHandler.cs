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
using Nethermind.Core.Specs;

namespace Nethermind.Evm
{
    public static class CodeDepositHandler
    {
        private const byte InvalidStartingCodeByte = 0xEF;
        public static long CalculateCost(int byteCodeLength, IReleaseSpec spec)
        {
            if (spec.LimitCodeSize  && byteCodeLength > spec.MaxCodeSize)
                return long.MaxValue;

            return GasCostOf.CodeDeposit * byteCodeLength;
        }

        public static bool CodeIsInvalid(IReleaseSpec spec, byte[] output)
        {
            return spec.IsEip3541Enabled && output.Length >= 1 && output[0] == InvalidStartingCodeByte;
        }
        
        public static bool CodeIsInvalid(IReleaseSpec spec, ReadOnlyMemory<byte> output)
        {
            return spec.IsEip3541Enabled && output.Length >= 1 && output.StartsWith(InvalidStartingCodeByte);
        }
    }
}
