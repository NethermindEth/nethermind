// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc
{
    public readonly struct RpcReport
    {
        public static readonly RpcReport Error = new RpcReport("# error #", 0, 0, false);

        public RpcReport(string method, long handlingTimeMicroseconds, long startTime, bool success)
        {
            Method = method;
            HandlingTimeMicroseconds = handlingTimeMicroseconds;
            StartTime = startTime;
            Success = success;
        }

        public long StartTime { get; }
        public string Method { get; }
        public long HandlingTimeMicroseconds { get; }
        public bool Success { get; }
    }
}
