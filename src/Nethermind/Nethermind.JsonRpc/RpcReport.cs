// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc
{
    public readonly struct RpcReport
    {
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
