// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core
{
    public static class RefundOf
    {
        public const ulong SSetReversedEip1283 = GasCostOf.SSet - GasCostOf.SStoreNetMeteredEip1283;
        public const ulong SResetReversedEip1283 = GasCostOf.SReset - GasCostOf.SStoreNetMeteredEip1283;
        public const ulong SSetReversedEip2200 = GasCostOf.SSet - GasCostOf.SStoreNetMeteredEip2200;
        public const ulong SResetReversedEip2200 = GasCostOf.SReset - GasCostOf.SStoreNetMeteredEip2200;
        public const ulong SSetReversedHotCold = GasCostOf.SSet - GasCostOf.WarmStateRead;
        public const ulong SResetReversedHotCold = GasCostOf.SReset - GasCostOf.ColdSLoad - GasCostOf.WarmStateRead;
        public const ulong SSetReversedEip8037 = GasCostOf.SSetState + GasCostOf.SSetRegular - GasCostOf.WarmStateRead;
        public const ulong SClearAfterEip3529 = GasCostOf.SReset - GasCostOf.ColdSLoad + GasCostOf.AccessStorageListEntry;
        public const ulong SClearBeforeEip3529 = 15000;
        public const ulong DestroyBeforeEip3529 = 24000;
        public const ulong DestroyAfterEip3529 = GasCostOf.Free;
    }
}
