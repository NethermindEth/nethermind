// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;

namespace Nethermind.JsonRpc.Modules.Eth.FeeHistory
{
    public interface IFeeHistoryOracle
    {
        ResultWrapper<FeeHistoryResults> GetFeeHistory(int blockCount, BlockParameter newestBlock, double[]? rewardPercentiles);

    }
}
