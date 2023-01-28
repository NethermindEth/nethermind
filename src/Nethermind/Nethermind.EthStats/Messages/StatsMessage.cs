// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
namespace Nethermind.EthStats.Messages
{
    public class StatsMessage : IMessage
    {
        public string? Id { get; set; }
        public Models.Stats Stats { get; }

        public StatsMessage(Models.Stats stats)
        {
            Stats = stats;
        }
    }
}
