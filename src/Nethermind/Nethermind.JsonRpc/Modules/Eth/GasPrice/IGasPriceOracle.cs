// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth.GasPrice
{
    public interface IGasPriceOracle
    {
        UInt256 GetGasPriceEstimate();
        UInt256 GetMaxPriorityGasFeeEstimate();
    }
}
