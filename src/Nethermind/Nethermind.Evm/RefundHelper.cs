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

        /// <summary>Returns the part of <paramref name="totalRefund"/> the transaction may claim against <paramref name="spentGas"/>.</summary>
        /// <remarks>EIP-3298 removes the cap, so the whole refund counter is claimable.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong CalculateClaimableRefund(ulong spentGas, ulong totalRefund, IReleaseSpec spec)
        {
            if (spec.IsEip3298Enabled) return totalRefund;
            ulong maxRefundQuotient = spec.IsEip3529Enabled ? MaxRefundQuotientEIP3529 : MaxRefundQuotient;
            return Math.Min(spentGas / maxRefundQuotient, totalRefund);
        }
    }
}
