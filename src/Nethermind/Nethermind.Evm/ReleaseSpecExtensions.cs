// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Evm
{
    public static class ReleaseSpecExtensions
    {
        extension(IReleaseSpec spec)
        {
            public int MaxProductionBlobCount(int? blockProductionBlobLimit) =>
                blockProductionBlobLimit >= 0
                    ? Math.Min(blockProductionBlobLimit.Value, (int)spec.MaxBlobCount)
                    : (int)spec.MaxBlobCount;

            public long GetBaseDataCost(Transaction tx) =>
                tx.IsContractCreation && spec.IsEip3860Enabled
                    ? EvmCalculations.Div32Ceiling((UInt256)tx.Data.Length) * GasCostOf.InitCodeWord
                    : 0;
        }
    }
}
