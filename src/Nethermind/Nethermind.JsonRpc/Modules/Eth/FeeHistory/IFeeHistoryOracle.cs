// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;

namespace Nethermind.JsonRpc.Modules.Eth.FeeHistory
{
    public interface IFeeHistoryOracle
    {
        ResultWrapper<FeeHistoryResults> GetFeeHistory(long blockCount, BlockParameter newestBlock, double[]? rewardPercentiles);

    }
}
