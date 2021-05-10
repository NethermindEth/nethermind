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
using System.Runtime.CompilerServices;
using Nethermind.Core.Specs;

namespace Nethermind.Evm
{
    public static class RefundHelper
    {
        public const long MaxRefundQuotient = 2L;
        
        public const long MaxRefundQuotientEIP3529 = 5L;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long CalculateClaimableRefund(long spentGas, long totalRefund, IReleaseSpec spec)
        {
            long maxRefundQuotient = spec.IsEip3529Enabled ? MaxRefundQuotientEIP3529 : MaxRefundQuotient;
            return Math.Min(spentGas / maxRefundQuotient, totalRefund);
        }
    }
}
