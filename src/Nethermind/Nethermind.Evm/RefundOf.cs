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
        public const long SClear = 15000;
        public const long Destroy = 24000;
    }
}
