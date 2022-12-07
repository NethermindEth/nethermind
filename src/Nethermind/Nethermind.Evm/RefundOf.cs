// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm
{
    public static class RefundOf
    {
        public const long SSetReversedEip1283 = GasCostOf.SSet - GasCostOf.SStoreNetMeteredEip1283;
        public const long SResetReversedEip1283 = GasCostOf.SReset - GasCostOf.SStoreNetMeteredEip1283;
        public const long SSetReversedEip2200 = GasCostOf.SSet - GasCostOf.SStoreNetMeteredEip2200;
        public const long SResetReversedEip2200 = GasCostOf.SReset - GasCostOf.SStoreNetMeteredEip2200;
        public const long SSetReversedHotCold = GasCostOf.SSet - GasCostOf.WarmStateRead;
        public const long SResetReversedHotCold = GasCostOf.SReset - GasCostOf.ColdSLoad - GasCostOf.WarmStateRead;
        private const long SClearAfter3529 = GasCostOf.SReset - GasCostOf.ColdSLoad + GasCostOf.AccessStorageListEntry;
        private const long SClearBefore3529 = 15000;
        private const long DestroyBefore3529 = 24000;
        private const long DestroyAfter3529 = 0;

        public static long SClear(bool eip3529Enabled)
        {
            return eip3529Enabled ? SClearAfter3529 : SClearBefore3529;
        }

        public static long Destroy(bool eip3529Enabled)
        {
            return eip3529Enabled ? DestroyAfter3529 : DestroyBefore3529;
        }
    }
}
