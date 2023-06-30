// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.Auth;

class CachedAuth : IAuth
{
    private readonly Lazy<string> _token;

    public CachedAuth(IAuth auth)
    {
        _token = new Lazy<string>(() => auth.AuthToken);
    }

    public string AuthToken
    {
        get => _token.Value;
    }

}
