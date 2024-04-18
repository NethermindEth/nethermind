// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

        public static long GetSha3Cost(this IReleaseSpec spec) =>
            spec.IsEip7667Enabled
                ? GasCostOf.Sha3Eip7667
                : GasCostOf.Sha3;

        public static long GetSha3WordCost(this IReleaseSpec spec) =>
            spec.IsEip7667Enabled
                ? GasCostOf.Sha3WordEip7667
                : GasCostOf.Sha3Word;

        public static long GetLogDataCost(this IReleaseSpec spec) =>
            spec.IsEip7667Enabled
                ? GasCostOf.LogDataEip7667
                : GasCostOf.LogData;

        public static long GetSha256PrecompileBaseCost(this IReleaseSpec spec) =>
            spec.IsEip7667Enabled
                ? 300L
                : 60L;

        public static long GetSha256PrecompileWordCost(this IReleaseSpec spec) =>
            spec.IsEip7667Enabled
                ? 60L
                : 12L;
    }
}
