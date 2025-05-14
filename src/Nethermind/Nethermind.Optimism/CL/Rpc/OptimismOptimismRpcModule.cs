// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.JsonRpc;

namespace Nethermind.Optimism.Cl.Rpc;

public class OptimismOptimismRpcModule : IOptimismOptimismRpcModule
{
    public ResultWrapper<int> optimism_outputAtBlock()
    {
        return ResultWrapper<int>.Success(0);
    }

    public ResultWrapper<int> optimism_rollupConfig()
    {
        return ResultWrapper<int>.Success(0);
    }

    public ResultWrapper<int> optimism_syncStatus()
    {
        return ResultWrapper<int>.Success(0);
    }

    public ResultWrapper<string> optimism_version()
    {
        return ResultWrapper<string>.Success(ProductInfo.Version);
    }
}
