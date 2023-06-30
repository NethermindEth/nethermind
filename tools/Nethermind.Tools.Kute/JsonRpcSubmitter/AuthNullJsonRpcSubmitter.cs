// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Tools.Kute.Auth;

namespace Nethermind.Tools.Kute.JsonRpcSubmitter;

class AuthNullJsonRpcSubmitter : IJsonRpcSubmitter
{
    private readonly IAuth _auth;
    private readonly NullJsonRpcSubmitter _submitter = new();

    public AuthNullJsonRpcSubmitter(IAuth auth)
    {
        _auth = auth;
    }

    public Task Submit(JsonRpc rpc)
    {
        _ = _auth.AuthToken;
        return _submitter.Submit(rpc);
    }
}
