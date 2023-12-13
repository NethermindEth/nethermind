// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Tools.Kute.Auth;

namespace Nethermind.Tools.Kute.JsonRpcSubmitter;

class NullJsonRpcSubmitter : IJsonRpcSubmitter
{
    private readonly IAuth _auth;

    public NullJsonRpcSubmitter(IAuth auth)
    {
        _auth = auth;
    }

    public Task Submit(JsonRpc rpc)
    {
        _ = _auth.AuthToken;
        return Task.CompletedTask;
    }
}
