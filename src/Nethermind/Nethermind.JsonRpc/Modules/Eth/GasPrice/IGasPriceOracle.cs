// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth.GasPrice
{
    public interface IGasPriceOracle
    {
        UInt256 GetGasPriceEstimate();
        UInt256 GetMaxPriorityGasFeeEstimate();

        ValueTask<UInt256> GetGasPriceEstimateAsync();

        ValueTask<UInt256> GetMaxPriorityGasFeeEstimateAsync();
    }
}
