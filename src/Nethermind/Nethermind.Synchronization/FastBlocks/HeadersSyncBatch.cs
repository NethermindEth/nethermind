// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Synchronization.FastBlocks
{
    public class HeadersSyncBatch : FastBlocksBatch
    {
        public long StartNumber { get; set; }
        public long EndNumber => StartNumber + RequestSize - 1;
        public int RequestSize { get; set; }
        public long ResponseSizeEstimate { get; private set; }

        private IOwnedReadOnlyList<BlockHeader?>? _response;
        public IOwnedReadOnlyList<BlockHeader?>? Response
        {
            get => _response;
            set
            {
                ResponseSizeEstimate = 0;
                if (value is not null)
                {
                    long size = 0;
                    foreach (BlockHeader response in value)
                    {
                        size += MemorySizeEstimator.EstimateSize(response);
                    }
                    ResponseSizeEstimate = size;
                }
                _response = value;
            }
        }

        public override string ToString()
        {
            string details = $"[{StartNumber}, {EndNumber}]({RequestSize})";
            return $"HEADERS {details} [{(Prioritized ? "HIGH" : "LOW")}] [times: S:{SchedulingTime:F0}ms|R:{RequestTime:F0}ms|V:{ValidationTime:F0}ms|W:{WaitingTime:F0}ms|H:{HandlingTime:F0}ms|A:{AgeInMs:F0}ms, retries {Retries}] min#: {MinNumber} {ResponseSourcePeer}";
        }

        public override long? MinNumber => EndNumber;

        public override void Dispose()
        {
            base.Dispose();
            Response?.Dispose();
        }
    }
}
