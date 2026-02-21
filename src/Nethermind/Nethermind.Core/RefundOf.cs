// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core
{
    public static class RefundOf
    {
        public const long SSetReversedEip1283 = GasCostOf.SSet - GasCostOf.SStoreNetMeteredEip1283;
        public const long SResetReversedEip1283 = GasCostOf.SReset - GasCostOf.SStoreNetMeteredEip1283;
        public const long SSetReversedEip2200 = GasCostOf.SSet - GasCostOf.SStoreNetMeteredEip2200;
        public const long SResetReversedEip2200 = GasCostOf.SReset - GasCostOf.SStoreNetMeteredEip2200;
        public const long SSetReversedHotCold = GasCostOf.SSet - GasCostOf.WarmStateRead;
        public const long SResetReversedHotCold = GasCostOf.SReset - GasCostOf.ColdSLoad - GasCostOf.WarmStateRead;
        public const long SClearAfter3529 = GasCostOf.SReset - GasCostOf.ColdSLoad + GasCostOf.AccessStorageListEntry;
        public const long SClearBefore3529 = 15000;
        public const long DestroyBefore3529 = 24000;
        public const long DestroyAfter3529 = 0;
    }
}
