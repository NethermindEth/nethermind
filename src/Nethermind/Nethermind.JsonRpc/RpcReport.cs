// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc
{
    public readonly struct RpcReport
    {
        public static readonly RpcReport Error = new RpcReport("# error #", 0, false);

        public RpcReport(string method, long handlingTimeMicroseconds, bool success)
        {
            Method = method;
            HandlingTimeMicroseconds = handlingTimeMicroseconds;
            Success = success;
        }

        public string Method { get; }
        public long HandlingTimeMicroseconds { get; }
        public bool Success { get; }
    }
}
