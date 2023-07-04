// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
