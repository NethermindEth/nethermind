// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Specs;

namespace Nethermind.Evm
{
    public static class RefundHelper
    {
        public const ulong MaxRefundQuotient = 2UL;

        public const ulong MaxRefundQuotientEIP3529 = 5UL;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong CalculateClaimableRefund(ulong spentGas, ulong totalRefund, IReleaseSpec spec)
        {
            ulong maxRefundQuotient = spec.IsEip3529Enabled ? MaxRefundQuotientEIP3529 : MaxRefundQuotient;
            return Math.Min(spentGas / maxRefundQuotient, totalRefund);
        }
    }
}
