// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core2.Configuration
{
    public class NetworkingConfiguration
    {
        public uint AttestationPropagationSlotRange { get; set; }
        public uint AttestationSubnetCount { get; set; }
        public uint GossipMaximumSize { get; set; }
        public uint MaximumChunkSize { get; set; }
        public TimeSpan MaximumGossipClockDisparity { get; set; }
        public TimeSpan ResponseTimeout { get; set; }
        public TimeSpan TimeToFirstByteTimeout { get; set; }
    }
}
