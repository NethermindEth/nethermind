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
// 

using System;
using Nethermind.Core.Specs;

namespace Nethermind.Evm
{
    public static class ReleaseSpecExtensions
    {
        public static long GetClearReversalRefund(this IReleaseSpec spec) =>
            spec.UseHotAndColdStorage
                ? RefundOf.SResetReversedHotCold
                : spec.UseIstanbulNetGasMetering
                    ? RefundOf.SResetReversedEip2200
                    : spec.UseConstantinopleNetGasMetering
                        ? RefundOf.SResetReversedEip1283
                        : throw new InvalidOperationException("Asking about the net metered cost when net metering not enabled");
        
        public static long GetSetReversalRefund(this IReleaseSpec spec) =>
            spec.UseHotAndColdStorage
                ? RefundOf.SSetReversedHotCold
                : spec.UseIstanbulNetGasMetering
                    ? RefundOf.SSetReversedEip2200
                    : spec.UseConstantinopleNetGasMetering
                        ? RefundOf.SSetReversedEip1283
                        : throw new InvalidOperationException("Asking about the net metered cost when net metering not enabled");

        public static long GetSStoreResetCost(this IReleaseSpec spec) =>
            spec.UseHotAndColdStorage
                ? GasCostOf.SReset - GasCostOf.ColdSLoad
                : GasCostOf.SReset;
        
        public static long GetNetMeteredSStoreCost(this IReleaseSpec spec) =>
            spec.UseHotAndColdStorage
                ? GasCostOf.WarmStateRead
                : spec.UseIstanbulNetGasMetering
                    ? GasCostOf.SStoreNetMeteredEip2200
                    : spec.UseConstantinopleNetGasMetering
                        ? GasCostOf.SStoreNetMeteredEip1283
                        : throw new InvalidOperationException("Asking about the net metered cost when net metering not enabled");

        public static long GetBalanceCost(this IReleaseSpec spec) =>
            spec.UseHotAndColdStorage
                ? 0L
                : spec.UseLargeStateDDosProtection
                    ? GasCostOf.BalanceEip1884
                    : spec.UseShanghaiDDosProtection
                        ? GasCostOf.BalanceEip150
                        : GasCostOf.Balance;

        public static long GetSLoadCost(this IReleaseSpec spec) =>
            spec.UseHotAndColdStorage
                ? 0L
                : spec.UseLargeStateDDosProtection
                    ? GasCostOf.SLoadEip1884
                    : spec.UseShanghaiDDosProtection
                        ? GasCostOf.SLoadEip150
                        : GasCostOf.SLoad;

        public static long GetExtCodeHashCost(this IReleaseSpec spec) =>
            spec.UseHotAndColdStorage
                ? 0L
                : spec.UseLargeStateDDosProtection
                    ? GasCostOf.ExtCodeHashEip1884
                    : GasCostOf.ExtCodeHash;
        
        public static long GetExtCodeCost(this IReleaseSpec spec) =>
            spec.UseHotAndColdStorage
                ? 0L
                : spec.UseShanghaiDDosProtection
                    ? GasCostOf.ExtCodeEip150
                    : GasCostOf.ExtCode;
        
        public static long GetCallCost(this IReleaseSpec spec) =>
            spec.UseHotAndColdStorage
                ? 0L
                : spec.UseShanghaiDDosProtection
                    ? GasCostOf.CallEip150
                    : GasCostOf.Call;

        public static long GetExpByteCost(this IReleaseSpec spec) =>
            spec.UseExpDDosProtection
                ? GasCostOf.ExpByteEip160
                : GasCostOf.ExpByte;
    }
}
