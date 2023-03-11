// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
