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
