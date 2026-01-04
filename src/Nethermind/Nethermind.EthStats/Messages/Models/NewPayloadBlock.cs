// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.EthStats.Messages.Models
{
    public class NewPayloadBlock
    {
        public long Number { get; }
        public string Hash { get; }
        public long ProcessingTime { get; }

        public NewPayloadBlock(long number, string hash, long processingTime)
        {
            Number = number;
            Hash = hash;
            ProcessingTime = processingTime;
        }
    }
}
