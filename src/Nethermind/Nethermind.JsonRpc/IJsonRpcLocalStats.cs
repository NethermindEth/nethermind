//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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
        void ReportCall(in RpcReport report, long elapsedMicroseconds = 0, long? size = null);

        public void ReportCalls(in IReadOnlyList<RpcReport> reports)
        {
            for (int i = 0; i < reports.Count; i++)
            {
                ReportCall(reports[i]);
            }
        }

        MethodStats GetMethodStats(string methodName);
    }
}
