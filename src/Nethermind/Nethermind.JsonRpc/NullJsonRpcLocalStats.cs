// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc;

public class NullJsonRpcLocalStats : IJsonRpcLocalStats
{

    public void ReportCall(RpcReport report, long elapsedMicroseconds = 0, long? size = null) { }
    public MethodStats GetMethodStats(string methodName) => new();
}
