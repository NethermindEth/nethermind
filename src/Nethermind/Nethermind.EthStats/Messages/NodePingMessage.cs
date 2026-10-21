// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Nethermind.EthStats.Messages
{
    public class NodePingMessage(long clientTime) : IMessage
    {
        public string? Id { get; set; }
        public long ClientTime { get; } = clientTime;
    }
}
