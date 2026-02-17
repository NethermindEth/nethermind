// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc
{
    public readonly record struct RpcReport(string Method, long HandlingTimeMicroseconds, bool Success)
    {
        public static readonly RpcReport Error = new("# error #", 0, false);
    }
}
