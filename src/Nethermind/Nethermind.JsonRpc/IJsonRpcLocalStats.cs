// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.JsonRpc
{
    public class MethodStats
    {
        public int Successes { get; set; }
        public int Errors { get; set; }
        public decimal AvgTimeOfErrors { get; set; }
        public decimal AvgTimeOfSuccesses { get; set; }
        public long MaxTimeOfError { get; set; }
        public long MaxTimeOfSuccess { get; set; }
        public decimal TotalSize { get; set; }
        public decimal AvgSize => Calls == 0 ? 0 : TotalSize / Calls;
        public int Calls => Successes + Errors;
    }

    public interface IJsonRpcLocalStats
    {
        void ReportCall(RpcReport report, long elapsedMicroseconds = 0, long? size = null);

        MethodStats GetMethodStats(string methodName);
    }
}
