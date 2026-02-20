// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm
{
    public static class ReleaseSpecExtensions
    {
        extension(IReleaseSpec spec)
        {
            public long GetClearReversalRefund() =>
                spec.UseHotAndColdStorage
                    ? RefundOf.SResetReversedHotCold
                    : spec.UseIstanbulNetGasMetering
                        ? RefundOf.SResetReversedEip2200
                        : spec.UseConstantinopleNetGasMetering
                            ? RefundOf.SResetReversedEip1283
                            : throw new InvalidOperationException("Asking about the net metered cost when net metering not enabled");

            public long GetSetReversalRefund() =>
                spec.UseHotAndColdStorage
                    ? RefundOf.SSetReversedHotCold
                    : spec.UseIstanbulNetGasMetering
                        ? RefundOf.SSetReversedEip2200
                        : spec.UseConstantinopleNetGasMetering
                            ? RefundOf.SSetReversedEip1283
                            : throw new InvalidOperationException("Asking about the net metered cost when net metering not enabled");

            public long GetSStoreResetCost() =>
                spec.UseHotAndColdStorage
                    ? GasCostOf.SReset - GasCostOf.ColdSLoad
                    : GasCostOf.SReset;

            public long GetNetMeteredSStoreCost() =>
                spec.UseHotAndColdStorage
                    ? GasCostOf.WarmStateRead
                    : spec.UseIstanbulNetGasMetering
                        ? GasCostOf.SStoreNetMeteredEip2200
                        : spec.UseConstantinopleNetGasMetering
                            ? GasCostOf.SStoreNetMeteredEip1283
                            : throw new InvalidOperationException("Asking about the net metered cost when net metering not enabled");

            public long GetBalanceCost() =>
                spec.UseHotAndColdStorage
                    ? 0L
                    : spec.UseLargeStateDDosProtection
                        ? GasCostOf.BalanceEip1884
                        : spec.UseShanghaiDDosProtection
                            ? GasCostOf.BalanceEip150
                            : GasCostOf.Balance;

            public long GetSLoadCost() =>
                spec.UseHotAndColdStorage
                    ? 0L
                    : spec.UseLargeStateDDosProtection
                        ? GasCostOf.SLoadEip1884
                        : spec.UseShanghaiDDosProtection
                            ? GasCostOf.SLoadEip150
                            : GasCostOf.SLoad;

            public long GetExtCodeHashCost() =>
                spec.UseHotAndColdStorage
                    ? 0L
                    : spec.UseLargeStateDDosProtection
                        ? GasCostOf.ExtCodeHashEip1884
                        : GasCostOf.ExtCodeHash;

            public long GetExtCodeCost() =>
                spec.UseHotAndColdStorage
                    ? 0L
                    : spec.UseShanghaiDDosProtection
                        ? GasCostOf.ExtCodeEip150
                        : GasCostOf.ExtCode;

            public long GetCallCost() =>
                spec.UseHotAndColdStorage
                    ? 0L
                    : spec.UseShanghaiDDosProtection
                        ? GasCostOf.CallEip150
                        : GasCostOf.Call;

            public long GetExpByteCost() =>
                spec.UseExpDDosProtection
                    ? GasCostOf.ExpByteEip160
                    : GasCostOf.ExpByte;

            public ulong GetMaxBlobGasPerBlock() =>
                spec.MaxBlobCount * Eip4844Constants.GasPerBlob;

            public ulong GetMaxBlobGasPerTx() =>
                spec.MaxBlobsPerTx * Eip4844Constants.GasPerBlob;

            public ulong GetTargetBlobGasPerBlock() =>
                spec.TargetBlobCount * Eip4844Constants.GasPerBlob;

            public int MaxProductionBlobCount(int? blockProductionBlobLimit) =>
                blockProductionBlobLimit >= 0
                    ? Math.Min(blockProductionBlobLimit.Value, (int)spec.MaxBlobCount)
                    : (int)spec.MaxBlobCount;

            public long GetTxDataNonZeroMultiplier() =>
                spec.IsEip2028Enabled ? GasCostOf.TxDataNonZeroMultiplierEip2028 : GasCostOf.TxDataNonZeroMultiplier;

            public long GetBaseDataCost(Transaction tx) =>
                tx.IsContractCreation && spec.IsEip3860Enabled
                    ? ((long)(tx.Data.Length + 31) / 32) * GasCostOf.InitCodeWord
                    : 0;

        }
    }
}
