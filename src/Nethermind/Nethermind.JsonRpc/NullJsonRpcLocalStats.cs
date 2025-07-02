// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;

namespace Nethermind.JsonRpc;

public class NullJsonRpcLocalStats : IJsonRpcLocalStats
{

    public Task ReportCall(RpcReport report, long elapsedMicroseconds = 0, long? size = null)
    {
        return Task.CompletedTask;
    }
    public MethodStats GetMethodStats(string methodName)
    {
        return new MethodStats();
    }
}
