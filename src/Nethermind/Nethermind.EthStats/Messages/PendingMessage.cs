// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.EthStats.Messages.Models;
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Nethermind.EthStats.Messages
{
    public class PendingMessage : IMessage
    {
        public string? Id { get; set; }
        public PendingStats Stats { get; }

        public PendingMessage(PendingStats stats)
        {
            Stats = stats;
        }
    }
}
