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

using Nethermind.Core;

namespace Nethermind.Synchronization.FastBlocks
{
    public class HeadersSyncBatch : FastBlocksBatch
    {
        public long StartNumber { get; set; }
        public long EndNumber => StartNumber + RequestSize - 1;
        public int RequestSize { get; set; }
        public BlockHeader?[]? Response { get; set; }

        public override string ToString()
        {
            string details = $"[{StartNumber}, {EndNumber}]({RequestSize})";
            return $"HEADERS {details} [{(Prioritized ? "HIGH" : "LOW")}] [times: S:{SchedulingTime:F0}ms|R:{RequestTime:F0}ms|V:{ValidationTime:F0}ms|W:{WaitingTime:F0}ms|H:{HandlingTime:F0}ms|A:{AgeInMs:F0}ms, retries {Retries}] min#: {MinNumber} {ResponseSourcePeer}";
        }
    }
}
