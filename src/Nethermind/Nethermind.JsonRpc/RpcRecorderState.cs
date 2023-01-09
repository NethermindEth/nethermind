// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.JsonRpc
{
    [Flags]
    public enum RpcRecorderState
    {
        None = 0,
        Request = 1,
        Response = 2,
        All = Request | Response
    }
}
