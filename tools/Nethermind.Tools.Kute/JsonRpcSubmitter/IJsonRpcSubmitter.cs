// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.JsonRpcSubmitter;

public interface IJsonRpcSubmitter
{
    Task<JsonRpc.Response> Submit(JsonRpc.Request rpc, CancellationToken token = default);
}
}
