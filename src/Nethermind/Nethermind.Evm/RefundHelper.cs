// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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
        public static ulong CalculateClaimableRefund(ulong spentGas, ulong totalRefund, IReleaseSpec spec)
        {
            ulong maxRefundQuotient = spec.IsEip3529Enabled ? (ulong)MaxRefundQuotientEIP3529 : (ulong)MaxRefundQuotient;
            ulong claimable = spentGas / maxRefundQuotient;
            return claimable < totalRefund ? claimable : totalRefund;
        }
    }
}
