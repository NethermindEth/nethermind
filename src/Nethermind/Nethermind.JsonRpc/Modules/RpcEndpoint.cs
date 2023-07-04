// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.JsonRpc.Modules
{
    [Flags]
    public enum RpcEndpoint
    {
        None = 0,
        Http = 1,
        Ws = 2,
        IPC = 4,
        Https = Http,
        Wss = Ws,
        All = Http | Ws | IPC
    }
}
