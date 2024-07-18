// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
namespace Nethermind.EthStats.Messages
{
    public class LatencyMessage : IMessage
    {
        public string? Id { get; set; }
        public long Latency { get; }

        public LatencyMessage(long latency)
        {
            Latency = latency;
        }
    }
}
