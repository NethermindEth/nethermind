// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Tools.Kute.JsonRpcSubmitter;

class NullJsonRpcSubmitter : IJsonRpcSubmitter
{

    public Task<HttpResponseMessage> Submit(JsonRpc rpc) => Task.FromResult<HttpResponseMessage>(null);

}
